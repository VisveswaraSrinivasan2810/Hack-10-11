using CivicLens.Domain.Configuration;

namespace CivicLens.Domain.Scoring;

public sealed record CorrelationMatch(string Type, double Confidence, string ConfidenceLabel, string Explanation);

/// <summary>
/// Rule: water leakage within 50 m, followed within 30 days by road damage / pothole =>
/// POSSIBLE relationship. Language is deliberately non-causal.
/// </summary>
public static class CorrelationRules
{
    public const string WaterLeakageToRoadDamage = "WaterLeakageToRoadDamage";

    public static string Label(double confidence) => confidence >= 0.7 ? "High" : confidence >= 0.45 ? "Moderate" : "Low";

    public static CorrelationMatch? TryCorrelate(
        IssueCategory sourceCategory, DateTime sourceDate, IssueCategory targetCategory, DateTime targetDate,
        double distanceMeters, ScoringOptions o)
    {
        if (sourceCategory != IssueCategory.WaterLeakage) return null;
        if (targetCategory is not (IssueCategory.RoadDamage or IssueCategory.Pothole)) return null;
        if (distanceMeters > o.CorrelationRadiusMeters) return null;
        var days = (targetDate - sourceDate).TotalDays;
        if (days <= 0 || days > o.CorrelationWindowDays) return null;

        var proximity = 1 - distanceMeters / o.CorrelationRadiusMeters;
        var timing = 1 - days / o.CorrelationWindowDays;
        var confidence = Math.Round(0.30 + 0.25 * proximity + 0.25 * timing, 2);
        var label = Label(confidence);
        var explanation =
            $"Possible infrastructure relationship: water leakage reported {days:0} days before " +
            $"{CategoryRules.Label(targetCategory).ToLowerInvariant()} {distanceMeters:0} m away. " +
            "Water leakage may have contributed to road deterioration; this is a potential relationship, not a confirmed cause.";
        return new CorrelationMatch(WaterLeakageToRoadDamage, confidence, label, explanation);
    }
}
