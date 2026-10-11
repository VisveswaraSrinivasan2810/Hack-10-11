using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace CivicLens.Infrastructure.Ai;

public sealed class ImageOptions
{
    /// <summary>Longest side kept for storage (pixels).</summary>
    public int MaxStoredSide { get; set; } = 2048;
    /// <summary>Longest side sent to AI vision (keeps well under provider size limits).</summary>
    public int MaxAiSide { get; set; } = 1568;
    public int JpegQuality { get; set; } = 85;
    /// <summary>When true, bytes that are not a decodable image are passed through (used by DEMO_MODE synthetic photos only).</summary>
    public bool AllowUndecodable { get; set; }
}

/// <summary>Shared Skia helpers: decode with EXIF orientation applied, resize, encode.</summary>
public static class ImageCodec
{
    public static SKBitmap? Decode(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;
            var origin = codec.EncodedOrigin;
            var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (bitmap is null) return null;
            return Orient(bitmap, origin);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src;
        float w0 = src.Width, h0 = src.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using (var canvas = new SKCanvas(dst))
        {
            // Matrix rows: [scaleX skewX transX; skewY scaleY transY]
            var m = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w0, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w0, 0, -1, h0, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h0, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h0, -1, 0, w0, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w0, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.SetMatrix(m);
            canvas.DrawBitmap(src, 0, 0);
        }
        src.Dispose();
        return dst;
    }

    public static SKBitmap Fit(SKBitmap src, int maxSide)
    {
        var longest = Math.Max(src.Width, src.Height);
        if (longest <= maxSide) return src;
        var scale = maxSide / (double)longest;
        var info = new SKImageInfo(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)), src.ColorType, src.AlphaType);
        var resized = src.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null) return src;
        src.Dispose();
        return resized;
    }

    public static byte[] EncodeJpeg(SKBitmap bmp, int quality)
    {
        // Flatten transparency onto white so PNG screenshots do not turn black.
        using var flat = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bmp, 0, 0);
        }
        using var image = SKImage.FromBitmap(flat);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}

public sealed class SkiaImageProcessor(IOptions<ImageOptions> options) : IImageProcessor
{
    private readonly ImageOptions _o = options.Value;

    public ProcessedImage Process(byte[] original)
    {
        var ext = ImageTools.ValidateAndGetExtension(original);
        var bmp = ImageCodec.Decode(original);
        if (bmp is null)
        {
            if (_o.AllowUndecodable) return new ProcessedImage(ImageTools.StripMetadata(original), ext, original, "image/jpeg");
            throw new BadRequestException("That file could not be read as a photo. Please try another photo.", "unsupported_image");
        }

        using (bmp)
        {
            var stored = ImageCodec.Fit(bmp, _o.MaxStoredSide);
            var storedBytes = ImageCodec.EncodeJpeg(stored, _o.JpegQuality);
            if (!ReferenceEquals(stored, bmp)) stored.Dispose();

            using var ai = ImageCodec.Decode(storedBytes)!;
            var aiFit = ImageCodec.Fit(ai, _o.MaxAiSide);
            var aiBytes = ReferenceEquals(aiFit, ai) ? storedBytes : ImageCodec.EncodeJpeg(aiFit, 80);
            if (!ReferenceEquals(aiFit, ai)) aiFit.Dispose();
            return new ProcessedImage(storedBytes, ".jpg", aiBytes, "image/jpeg");
        }
    }
}

/// <summary>
/// Perceptual similarity: a 16x16 luminance thumbnail, mean/variance-normalised, compared by Pearson correlation.
/// Robust to exposure differences; undecodable bytes (demo synthetic photos) fall back to a byte-histogram signature.
/// </summary>
public sealed class ImageSimilarityService : IImageSimilarityService
{
    private const int Grid = 16;
    private const int Buckets = 64;

    public string ComputeSignature(byte[] imageBytes)
    {
        using var bmp = ImageCodec.Decode(imageBytes);
        return bmp is null ? HistogramSignature(imageBytes) : "t1:" + ThumbnailSignature(bmp);
    }

    public Task<double> CompareAsync(string signatureA, string hashA, string signatureB, string hashB, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(hashA) && hashA == hashB) return Task.FromResult(1.0);
        if (signatureA.StartsWith("t1:") && signatureB.StartsWith("t1:"))
        {
            var a = ParseThumb(signatureA[3..]); var b = ParseThumb(signatureB[3..]);
            if (a is null || b is null) return Task.FromResult(0.5);
            var r = Pearson(a, b);
            // unrelated scenes ~0.1, same scene from a similar angle ~0.6-0.95
            return Task.FromResult(Math.Round(0.10 + 0.85 * Math.Clamp(r, 0, 1), 3));
        }
        return HistogramSimilarity(signatureA, signatureB);
    }

    private static string ThumbnailSignature(SKBitmap src)
    {
        using var small = src.Resize(new SKImageInfo(Grid, Grid, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                          ?? throw new InvalidOperationException("resize failed");
        var lum = new double[Grid * Grid];
        for (var y = 0; y < Grid; y++)
            for (var x = 0; x < Grid; x++)
            {
                var c = small.GetPixel(x, y);
                lum[y * Grid + x] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            }
        var mean = lum.Average();
        var sd = Math.Sqrt(lum.Sum(v => (v - mean) * (v - mean)) / lum.Length);
        if (sd < 1e-6) sd = 1;
        return string.Concat(lum.Select(v => ((int)Math.Round(Math.Clamp((v - mean) / sd, -3, 3) * 42.5 + 127.5)).ToString("x2")));
    }

    private static double[]? ParseThumb(string hex)
    {
        if (hex.Length != Grid * Grid * 2) return null;
        var r = new double[Grid * Grid];
        for (var i = 0; i < r.Length; i++) r[i] = Convert.ToInt32(hex.Substring(i * 2, 2), 16);
        return r;
    }

    private static double Pearson(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), num = 0, da = 0, db = 0;
        for (var i = 0; i < a.Length; i++) { num += (a[i] - ma) * (b[i] - mb); da += (a[i] - ma) * (a[i] - ma); db += (b[i] - mb) * (b[i] - mb); }
        return da <= 0 || db <= 0 ? 0 : num / Math.Sqrt(da * db);
    }

    private static string HistogramSignature(byte[] bytes)
    {
        var counts = new double[Buckets];
        foreach (var b in bytes) counts[b * Buckets / 256]++;
        var max = Math.Max(1, counts.Max());
        return string.Concat(counts.Select(c => ((int)Math.Round(c / max * 255)).ToString("x2")));
    }

    private static Task<double> HistogramSimilarity(string a, string b)
    {
        if (a.Length != Buckets * 2 || b.Length != Buckets * 2) return Task.FromResult(0.5);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Buckets; i++)
        {
            var x = Convert.ToInt32(a.Substring(i * 2, 2), 16); var y = Convert.ToInt32(b.Substring(i * 2, 2), 16);
            dot += x * y; na += x * x; nb += y * y;
        }
        var cos = na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
        return Task.FromResult(Math.Round(0.15 + 0.80 * Math.Clamp((cos - 0.80) / 0.20, 0, 1), 3));
    }
}
