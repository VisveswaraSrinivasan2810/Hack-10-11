using System.Security.Cryptography;

namespace CivicLens.Application.Common;

public static class ImageTools
{
    public const int MaxBytes = 15 * 1024 * 1024;

    /// <summary>Validates by magic bytes (never trusts the client's content-type). Returns the file extension.</summary>
    public static string ValidateAndGetExtension(byte[] bytes)
    {
        if (bytes.Length == 0) throw new BadRequestException("The uploaded file is empty.", "invalid_image");
        if (bytes.Length > MaxBytes) throw new BadRequestException("The image is too large. Please upload a photo under 15 MB.", "image_too_large");
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return ".webp";
        if (bytes.Length >= 12 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
            throw new BadRequestException("HEIC/HEIF photos are not supported yet. Please switch your camera format to Most Compatible (JPEG) or upload a JPEG/PNG.", "unsupported_image");
        throw new BadRequestException("Unsupported image. Please upload a JPEG, PNG or WebP photo.", "unsupported_image");
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Removes EXIF/XMP (JPEG APP1/APP13) and textual PNG chunks so GPS/device metadata is never stored.</summary>
    public static byte[] StripMetadata(byte[] bytes)
    {
        try
        {
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return StripJpeg(bytes);
            if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50) return StripPng(bytes);
        }
        catch (Exception)
        {
            // Malformed container: keep original bytes rather than failing the citizen's report.
        }
        return bytes;
    }

    private static byte[] StripJpeg(byte[] src)
    {
        using var ms = new MemoryStream(src.Length);
        ms.Write(src, 0, 2); // SOI
        var i = 2;
        while (i + 4 <= src.Length)
        {
            if (src[i] != 0xFF) break;
            var marker = src[i + 1];
            if (marker == 0xDA) break; // start of scan: rest is entropy-coded data
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { ms.Write(src, i, 2); i += 2; continue; }
            var len = (src[i + 2] << 8) | src[i + 3];
            if (len < 2 || i + 2 + len > src.Length) return src;
            var drop = marker is 0xE1 or 0xED or 0xFE; // APP1 (EXIF/XMP), APP13 (IPTC), COM
            if (!drop) ms.Write(src, i, 2 + len);
            i += 2 + len;
        }
        ms.Write(src, i, src.Length - i);
        return ms.ToArray();
    }

    private static byte[] StripPng(byte[] src)
    {
        using var ms = new MemoryStream(src.Length);
        ms.Write(src, 0, 8);
        var i = 8;
        while (i + 12 <= src.Length)
        {
            var len = (src[i] << 24) | (src[i + 1] << 16) | (src[i + 2] << 8) | src[i + 3];
            if (len < 0 || i + 12 + len > src.Length) return src;
            var type = System.Text.Encoding.ASCII.GetString(src, i + 4, 4);
            if (type is not ("eXIf" or "tEXt" or "iTXt" or "zTXt")) ms.Write(src, i, 12 + len);
            i += 12 + len;
        }
        return ms.ToArray();
    }
}
