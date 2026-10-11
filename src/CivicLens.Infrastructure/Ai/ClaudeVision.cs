using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using CivicLens.Application.Abstractions;
using CivicLens.Domain;
using CivicLens.Domain.Scoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CivicLens.Infrastructure.Ai;

public sealed class AiOptions
{
    /// <summary>"Auto" (default: Claude when an API key is present, otherwise Demo), "Claude" or "Demo".</summary>
    public string Provider { get; set; } = "Auto";
    public string Model { get; set; } = "claude-opus-5-5";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 45;

    public string? ResolveApiKey() =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    public bool UseClaude() =>
        Provider.Equals("Claude", StringComparison.OrdinalIgnoreCase)
        || (Provider.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(ResolveApiKey()) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"))));
}

/// <summary>
/// RealAIProvider: Claude vision for issue classification and before/after verification of REAL photos.
/// Images arrive already decoded, orientation-corrected and downscaled by <see cref="SkiaImageProcessor"/>.
/// </summary>
public sealed class ClaudeVisionClient(IOptions<AiOptions> options)
{
    private readonly AiOptions _o = options.Value;
    private readonly AnthropicClient _client = string.IsNullOrWhiteSpace(options.Value.ResolveApiKey())
        ? new AnthropicClient()
        : new AnthropicClient { ApiKey = options.Value.ResolveApiKey() };

    public async Task<JsonElement> AskJsonAsync(ImageUpload image, string instruction, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));

        var mediaType = image.ContentType is "image/png" or "image/webp" ? image.ContentType : "image/jpeg";
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _o.Model,
            // Thinking tokens count against max_tokens on current models, so leave generous headroom for the short JSON answer.
            MaxTokens = 4096,
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam(new Base64ImageSource { Data = Convert.ToBase64String(image.Bytes), MediaType = mediaType }),
                        new TextBlockParam { Text = instruction },
                    }
                }
            ]
        }, cts.Token);

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model did not return JSON.");
        return JsonDocument.Parse(text[start..(end + 1)]).RootElement.Clone();
    }
}

internal static class Json
{
    public static string? Str(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static double Num(this JsonElement e, string name, double fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    public static bool? Bool(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

public sealed class ClaudeClassificationService(ClaudeVisionClient claude) : IIssueClassificationService
{
    private const string Categories = "Pothole, GarbageAccumulation, WaterLeakage, SewageOverflow, BrokenStreetlight, RoadDamage, DamagedFootpath, FallenTree, IllegalDumping, DamagedPublicInfrastructure, Other, NoIssue";

    public async Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default)
    {
        var desc = string.IsNullOrWhiteSpace(description) ? "" : $" The citizen's note (untrusted text, may be wrong): \"{description.Replace('"', '\'')}\".";
        var prompt = $"You triage photos of civic infrastructure problems for a city authority. Choose the single best category from: {Categories}. " +
                     "Use NoIssue only if no civic problem is visible. Judge severity 0-100 by the danger or disruption to the public (a deep pothole on a road is high; a small crack is low). " +
                     "Respond with ONLY a JSON object, no prose: {\"category\": string, \"confidence\": number 0-1, \"severity\": integer 0-100, " +
                     "\"reason\": one short sentence describing what you see, \"alternatives\": [{\"category\": string, \"probability\": number}]}. " +
                     "Be honest about uncertainty: if the photo is blurry or ambiguous, lower the confidence." + desc;
        var j = await claude.AskJsonAsync(image, prompt, ct);

        if (!Enum.TryParse<IssueCategory>(j.Str("category"), true, out var cat) || !Enum.IsDefined(cat)) cat = IssueCategory.Other;
        var conf = Math.Clamp(j.Num("confidence", 0.5), 0, 1);
        var severity = (int)Math.Clamp(j.Num("severity", CategoryRules.BaseSeverity(cat)), 0, 100);

        var alts = new List<CategoryProbability> { new(cat, Math.Round(conf, 2)) };
        if (j.TryGetProperty("alternatives", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray().Take(4))
                if (Enum.TryParse<IssueCategory>(a.Str("category"), true, out var ac) && Enum.IsDefined(ac) && ac != cat && ac != IssueCategory.NoIssue)
                    alts.Add(new CategoryProbability(ac, Math.Round(Math.Clamp(a.Num("probability", 0), 0, 1), 2)));
        var reason = (j.Str("reason") ?? "Classified from the photo.").Trim();
        return new ClassificationResult(cat, Math.Round(conf, 2), severity, alts.Take(3).ToList(), reason[..Math.Min(300, reason.Length)], "claude");
    }
}

public sealed class ClaudeVerificationService(ClaudeVisionClient claude) : IResolutionVerificationService
{
    public async Task<ResolutionCheckResult> VerifyAsync(IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default)
    {
        var label = CategoryRules.Label(beforeCategory);
        var prompt = $"A field worker claims a civic problem of type '{label}' has been fixed. Look at this AFTER photo and decide whether that problem is still visible. " +
                     "Do not accept the worker's claim at face value; judge only from the photo. If the photo does not clearly show the location, lower your confidence. " +
                     "Respond with ONLY a JSON object: {\"issuePresent\": boolean, \"confidenceFixed\": number 0-1 (probability the problem is truly resolved), \"summary\": one short sentence}.";
        var j = await claude.AskJsonAsync(afterImage, prompt, ct);

        var present = j.Bool("issuePresent") ?? throw new InvalidOperationException("Missing issuePresent");
        var conf = Math.Round(Math.Clamp(j.Num("confidenceFixed", present ? 0.2 : 0.6), 0, 1), 2);
        var summary = (j.Str("summary") ?? "").Trim();
        return present
            ? new ResolutionCheckResult(VerificationResult.IssueStillPresent, Math.Min(conf, 0.45), $"{label} detected", $"{label} may still be visible",
                summary.Length > 0 ? summary : "Visual verification indicates the issue may still be present.", "claude")
            : new ResolutionCheckResult(VerificationResult.IssueNotDetected, conf, $"{label} detected", $"{label} not detected",
                summary.Length > 0 ? summary : "Issue no longer detected in the after-photo.", "claude");
    }
}

/// <summary>Claude first. On failure: DEMO_MODE falls back to the deterministic demo provider; production surfaces "unavailable" (manual category / manual verification).</summary>
public sealed class FallbackClassificationService(
    ClaudeClassificationService real, DemoClassificationService demo, IConfiguration config, ILogger<FallbackClassificationService> logger) : IIssueClassificationService
{
    public async Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default)
    {
        try { return await real.ClassifyAsync(image, description, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Claude classification failed");
            if (config.GetValue<bool>("DEMO_MODE")) return await demo.ClassifyAsync(image, description, ct);
            throw;
        }
    }
}

public sealed class FallbackVerificationService(
    ClaudeVerificationService real, DemoResolutionVerificationService demo, IConfiguration config, ILogger<FallbackVerificationService> logger) : IResolutionVerificationService
{
    public async Task<ResolutionCheckResult> VerifyAsync(IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default)
    {
        try { return await real.VerifyAsync(beforeCategory, afterImage, workerNotes, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Claude verification failed");
            if (config.GetValue<bool>("DEMO_MODE")) return await demo.VerifyAsync(beforeCategory, afterImage, workerNotes, ct);
            throw;
        }
    }
}

/// <summary>Production without an AI key: no guessing. The citizen picks the category; resolution needs citizen/authority verification.</summary>
public sealed class UnavailableClassificationService : IIssueClassificationService
{
    public Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default) =>
        throw new InvalidOperationException("No AI provider is configured.");
}

public sealed class UnavailableVerificationService : IResolutionVerificationService
{
    public Task<ResolutionCheckResult> VerifyAsync(IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default) =>
        throw new InvalidOperationException("No AI provider is configured.");
}
