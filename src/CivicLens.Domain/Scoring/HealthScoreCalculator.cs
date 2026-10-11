namespace CivicLens.Domain.Scoring;

public sealed record ZoneStats(int ActiveIncidents, int CriticalIncidents, int RecurringIncidents, int DisputedResolutions, double AverageResolutionHours);

public sealed record HealthResult(double Score, HealthBand Band, IReadOnlyList<string> Penalties);

/// <summary>Civic Health Score: 100 minus capped, transparent penalties. Higher = healthier.</summary>
public static class HealthScoreCalculator
{
    public static HealthBand BandFor(double score) => score >= 80 ? HealthBand.Healthy : score >= 60 ? HealthBand.NeedsAttention : HealthBand.Critical;

    public static string Label(HealthBand b) => b switch
    {
        HealthBand.Healthy => "HEALTHY",
        HealthBand.NeedsAttention => "NEEDS ATTENTION",
        _ => "CRITICAL"
    };

    public static HealthResult Calculate(ZoneStats s)
    {
        var density = Math.Min(35, s.ActiveIncidents * 3.0);
        var critical = Math.Min(25, s.CriticalIncidents * 8.0);
        var recurring = Math.Min(15, s.RecurringIncidents * 5.0);
        var disputed = Math.Min(15, s.DisputedResolutions * 8.0);
        var slow = Math.Min(10, Math.Max(0, s.AverageResolutionHours - 48) / 12.0);

        var score = Math.Round(Math.Clamp(100 - density - critical - recurring - disputed - slow, 0, 100), 1);
        var penalties = new List<string>();
        if (density > 0) penalties.Add($"{s.ActiveIncidents} unresolved incidents: -{density:0.#}");
        if (critical > 0) penalties.Add($"{s.CriticalIncidents} critical incidents: -{critical:0.#}");
        if (recurring > 0) penalties.Add($"{s.RecurringIncidents} recurring issues: -{recurring:0.#}");
        if (disputed > 0) penalties.Add($"{s.DisputedResolutions} disputed resolutions: -{disputed:0.#}");
        if (slow > 0) penalties.Add($"Average resolution {s.AverageResolutionHours:0} h: -{slow:0.#}");
        return new HealthResult(score, BandFor(score), penalties);
    }
}
