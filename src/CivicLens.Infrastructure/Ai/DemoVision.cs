using System.Security.Cryptography;
using CivicLens.Application.Abstractions;
using CivicLens.Domain;
using CivicLens.Domain.Scoring;

namespace CivicLens.Infrastructure.Ai;

/// <summary>
/// Deterministic "vision" used by DemoAIProvider. No network, no models: results are derived from file-name /
/// description hints (e.g. "pothole.jpg", "after_fixed.jpg", "still_pothole.jpg") plus stable jitter from the image hash,
/// so the same demo input always produces the same, realistic-looking output.
/// </summary>
public static class DemoVision
{
    private static readonly (string[] Tokens, IssueCategory Category)[] Keywords =
    [
        (["pothole"], IssueCategory.Pothole),
        (["sewage", "overflow", "manhole"], IssueCategory.SewageOverflow),
        (["leak", "water", "pipe", "burst"], IssueCategory.WaterLeakage),
        (["streetlight", "street_light", "lamp", "lightpost"], IssueCategory.BrokenStreetlight),
        (["dump"], IssueCategory.IllegalDumping),
        (["garbage", "trash", "waste", "rubbish", "litter"], IssueCategory.GarbageAccumulation),
        (["footpath", "pavement", "sidewalk"], IssueCategory.DamagedFootpath),
        (["tree", "fallen"], IssueCategory.FallenTree),
        (["road_damage", "roaddamage", "crack", "asphalt", "damaged_road"], IssueCategory.RoadDamage),
        (["bench", "signal", "railing", "bridge", "infrastructure"], IssueCategory.DamagedPublicInfrastructure),
    ];

    private static readonly string[] FixedTokens = ["fixed", "repaired", "resolved", "cleaned", "cleared", "filled", "restored", "after_ok"];
    private static readonly string[] StillTokens = ["still", "unresolved", "notfixed", "not_fixed", "ghost", "unfixed", "remains"];
    private static readonly string[] SevereTokens = ["severe", "dangerous", "deep", "huge", "large", "major"];
    private static readonly string[] MinorTokens = ["minor", "small", "slight", "tiny"];

    public sealed record Hint(IssueCategory? Category, bool Strong, bool IsFixed, bool StillPresent, int SeverityDelta);

    public static Hint Analyze(string? fileName, string? description)
    {
        var name = Normalize(fileName is null ? "" : Path.GetFileNameWithoutExtension(fileName));
        var desc = Normalize(description ?? "");
        var all = name + " " + desc;

        var isFixed = FixedTokens.Any(name.Contains);
        var still = StillTokens.Any(name.Contains);
        var severity = SevereTokens.Any(all.Contains) ? 10 : MinorTokens.Any(all.Contains) ? -15 : 0;

        foreach (var (tokens, cat) in Keywords)
            if (tokens.Any(name.Contains)) return new Hint(cat, true, isFixed && !still, still, severity);
        foreach (var (tokens, cat) in Keywords)
            if (tokens.Any(desc.Contains)) return new Hint(cat, false, isFixed && !still, still, severity);
        return new Hint(null, false, isFixed && !still, still, severity);
    }

    private static string Normalize(string s) => s.ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    /// <summary>Stable 0..255 value derived from the image bytes.</summary>
    public static int Jitter(byte[] bytes, int offset = 0) => SHA256.HashData(bytes)[offset % 32];

    public static IssueCategory Partner(IssueCategory c) => c switch
    {
        IssueCategory.Pothole => IssueCategory.RoadDamage,
        IssueCategory.RoadDamage => IssueCategory.Pothole,
        IssueCategory.GarbageAccumulation => IssueCategory.IllegalDumping,
        IssueCategory.IllegalDumping => IssueCategory.GarbageAccumulation,
        IssueCategory.WaterLeakage => IssueCategory.SewageOverflow,
        IssueCategory.SewageOverflow => IssueCategory.WaterLeakage,
        IssueCategory.BrokenStreetlight => IssueCategory.DamagedPublicInfrastructure,
        IssueCategory.FallenTree => IssueCategory.DamagedPublicInfrastructure,
        IssueCategory.DamagedFootpath => IssueCategory.RoadDamage,
        _ => IssueCategory.Other
    };

    public static IReadOnlyList<CategoryProbability> Distribution(IssueCategory primary, double p)
    {
        var rest = 1 - p;
        var partner = Partner(primary);
        var list = new List<CategoryProbability> { new(primary, Math.Round(p, 2)) };
        if (partner != IssueCategory.Other)
        {
            list.Add(new(partner, Math.Round(rest * 0.65, 2)));
            list.Add(new(IssueCategory.Other, Math.Round(rest * 0.35, 2)));
        }
        else list.Add(new(IssueCategory.Other, Math.Round(rest, 2)));
        return list;
    }
}

public sealed class DemoClassificationService : IIssueClassificationService
{
    private static readonly IssueCategory[] Fallbacks =
    [
        IssueCategory.RoadDamage, IssueCategory.Pothole, IssueCategory.GarbageAccumulation, IssueCategory.WaterLeakage,
        IssueCategory.BrokenStreetlight, IssueCategory.DamagedFootpath, IssueCategory.SewageOverflow, IssueCategory.FallenTree
    ];

    public Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default)
    {
        var hint = DemoVision.Analyze(image.FileName, description);
        var j = DemoVision.Jitter(image.Bytes);

        if (hint.IsFixed)
            return Task.FromResult(new ClassificationResult(IssueCategory.NoIssue, 0.9, 0, [], "No visible civic issue detected (demo vision).", "demo"));

        if (hint.Category is { } cat)
        {
            var conf = hint.Strong ? 0.94 + j % 5 / 100.0 : 0.86 + j % 5 / 100.0;
            var severity = Math.Clamp(CategoryRules.BaseSeverity(cat) + j % 9 - 4 + hint.SeverityDelta, 5, 100);
            var reason = hint.Strong
                ? $"Visual features consistent with {CategoryRules.Label(cat).ToLowerInvariant()} (demo vision, filename cue)."
                : $"Description mentions {CategoryRules.Label(cat).ToLowerInvariant()}; photo not conclusive on its own (demo vision).";
            return Task.FromResult(new ClassificationResult(cat, Math.Round(conf, 2), severity, DemoVision.Distribution(cat, conf), reason, "demo"));
        }

        // No cue at all: behave like an unsure model so the low-confidence UX is demonstrable.
        var primary = Fallbacks[j % Fallbacks.Length];
        var second = DemoVision.Partner(primary) is var p && p != IssueCategory.Other ? p : Fallbacks[(j + 1) % Fallbacks.Length];
        var alt = new List<CategoryProbability> { new(primary, 0.51), new(second, 0.32), new(IssueCategory.Other, 0.17) };
        return Task.FromResult(new ClassificationResult(primary, 0.51, CategoryRules.BaseSeverity(primary) - 5, alt,
            "The photo does not clearly match a single category (demo vision).", "demo"));
    }
}

public sealed class DemoResolutionVerificationService : IResolutionVerificationService
{
    public Task<ResolutionCheckResult> VerifyAsync(IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default)
    {
        var hint = DemoVision.Analyze(afterImage.FileName, null);
        var j = DemoVision.Jitter(afterImage.Bytes, 3);
        var label = CategoryRules.Label(beforeCategory);

        var stillThere = hint.StillPresent || (!hint.IsFixed && hint.Category is { } c && CategoryRules.Similarity(c, beforeCategory) > 0);
        if (stillThere)
        {
            var conf = Math.Round(0.18 + j % 12 / 100.0, 2); // 0.18 – 0.29
            return Task.FromResult(new ResolutionCheckResult(VerificationResult.IssueStillPresent, conf,
                $"{label} detected", $"{label} may still be visible",
                "Visual verification indicates the issue may still be present.", "demo"));
        }

        var ok = Math.Round(0.92 + j % 7 / 100.0, 2); // 0.92 – 0.98
        return Task.FromResult(new ResolutionCheckResult(VerificationResult.IssueNotDetected, ok,
            $"{label} detected", $"{label} not detected", "Issue no longer detected in the after-photo.", "demo"));
    }
}
