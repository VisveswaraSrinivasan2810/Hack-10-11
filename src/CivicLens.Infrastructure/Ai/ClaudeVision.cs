using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using CivicLens.Application.Abstractions;
using CivicLens.Domain;
using CivicLens.Domain.Scoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CivicLens.Infrastructure.Ai;

public sealed class AiOptions
{
    /// <summary>"Demo" (offline, deterministic) or "Claude" (real vision, falls back to Demo on any failure).</summary>
    public string Provider { get; set; } = "Demo";
    public string Model { get; set; } = "claude-opus-5-5";
    public string? ApiKey { get; set; }
}

/// <summary>
/// RealAIProvider: Claude vision for issue classification and before/after verification.
/// Every call is wrapped by <see cref="FallbackClassificationService"/> / <see cref="FallbackVerificationService"/>,
/// so an outage, bad key or malformed answer silently degrades to the deterministic demo provider.
/// </summary>
public sealed class ClaudeVisionClient(IOptions<AiOptions> options)
{
    private readonly AiOptions _o = options.Value;
    private readonly AnthropicClient _client = string.IsNullOrWhiteSpace(options.Value.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.Value.ApiKey };

    public async Task<JsonElement> AskJsonAsync(ImageUpload image, string instruction, CancellationToken ct)
    {
        var mediaType = image.Bytes.Length > 3 && image.Bytes[0] == 0x89 ? "image/png"
            : image.Bytes.Length > 11 && image.Bytes[0] == 'R' ? "image/webp" : "image/jpeg";

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _o.Model,
            MaxTokens = 1024,
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
        }, ct);

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model did not return JSON.");
        return JsonDocument.Parse(text[start..(end + 1)]).RootElement.Clone();
    }
}

public sealed class ClaudeClassificationService(ClaudeVisionClient claude) : IIssueClassificationService
{
    private const string Categories = "Pothole, GarbageAccumulation, WaterLeakage, SewageOverflow, BrokenStreetlight, RoadDamage, DamagedFootpath, FallenTree, IllegalDumping, DamagedPublicInfrastructure, Other, NoIssue";

    public async Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default)
    {
        var prompt = $"You triage civic infrastructure photos. Choose the single best category from: {Categories}. Use NoIssue if no civic problem is visible. " +
                     "Respond with ONLY a JSON object: {\"category\": string, \"confidence\": number 0-1, \"severity\": integer 0-100, \"reason\": one short sentence, " +
                     "\"alternatives\": [{\"category\": string, \"probability\": number}]}. Be honest about uncertainty. " +
                     (string.IsNullOrWhiteSpace(description) ? "" : $"Citizen description: {description}");
        var j = await claude.AskJsonAsync(image, prompt, ct);

        if (!Enum.TryParse<IssueCategory>(j.GetProperty("category").GetString(), true, out var cat)) cat = IssueCategory.Other;
        var conf = Math.Clamp(j.GetProperty("confidence").GetDouble(), 0, 1);
        var severity = Math.Clamp(j.GetProperty("severity").GetInt32(), 0, 100);
        var alts = new List<CategoryProbability>();
        if (j.TryGetProperty("alternatives", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray().Take(3))
                if (Enum.TryParse<IssueCategory>(a.GetProperty("category").GetString(), true, out var ac))
                    alts.Add(new CategoryProbability(ac, Math.Round(Math.Clamp(a.GetProperty("probability").GetDouble(), 0, 1), 2)));
        if (alts.Count == 0) alts.Add(new CategoryProbability(cat, conf));
        return new ClassificationResult(cat, Math.Round(conf, 2), severity, alts, j.GetProperty("reason").GetString() ?? "", "claude");
    }
}

public sealed class ClaudeVerificationService(ClaudeVisionClient claude) : IResolutionVerificationService
{
    public async Task<ResolutionCheckResult> VerifyAsync(IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default)
    {
        var label = CategoryRules.Label(beforeCategory);
        var prompt = $"A field worker says a civic problem of type '{label}' was fixed. Look at this AFTER photo and decide whether that problem is still visible. " +
                     "Respond with ONLY a JSON object: {\"issuePresent\": boolean, \"confidenceFixed\": number 0-1 (probability the problem is truly resolved), \"summary\": one short sentence}. " +
                     "Do not accept the worker's claim at face value.";
        var j = await claude.AskJsonAsync(afterImage, prompt, ct);
        var present = j.GetProperty("issuePresent").GetBoolean();
        var conf = Math.Round(Math.Clamp(j.GetProperty("confidenceFixed").GetDouble(), 0, 1), 2);
        var summary = j.GetProperty("summary").GetString() ?? "";
        return present
            ? new ResolutionCheckResult(VerificationResult.IssueStillPresent, Math.Min(conf, 0.45), $"{label} detected", $"{label} may still be visible", summary, "claude")
            : new ResolutionCheckResult(VerificationResult.IssueNotDetected, conf, $"{label} detected", $"{label} not detected", summary, "claude");
    }
}

/// <summary>Real provider first; any failure (network, key, malformed JSON) falls back to the deterministic demo provider.</summary>
public sealed class FallbackClassificationService(
    ClaudeClassificationService real, DemoClassificationService demo, ILogger<FallbackClassificationService> logger) : IIssueClassificationService
{
    public async Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default)
    {
        try { return await real.ClassifyAsync(image, description, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Claude classification failed; using demo provider");
            return await demo.ClassifyAsync(image, description, ct);
        }
    }
}

public sealed class FallbackVerificationService(
    ClaudeVerificationService real, DemoResolutionVerificationService demo, ILogger<FallbackVerificationService> logger) : IResolutionVerificationService
{
    public async Task<ResolutionCheckResult> VerifyAsync(IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default)
    {
        try { return await real.VerifyAsync(beforeCategory, afterImage, workerNotes, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Claude verification failed; using demo provider");
            return await demo.VerifyAsync(beforeCategory, afterImage, workerNotes, ct);
        }
    }
}
