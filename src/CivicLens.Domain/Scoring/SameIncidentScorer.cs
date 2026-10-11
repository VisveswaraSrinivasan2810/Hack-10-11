using CivicLens.Domain.Configuration;

namespace CivicLens.Domain.Scoring;

public sealed record SameIncidentScore(
    double Total, double Spatial, double Category, double Visual, double Temporal,
    MatchVerdict Verdict, double DistanceMeters, string Reason);

/// <summary>
/// Civic Incident Fingerprint:
/// SameIncidentScore = 0.40*spatial + 0.30*category + 0.20*visual + 0.10*temporal (all 0..1).
/// </summary>
public static class SameIncidentScorer
{
    public static SameIncidentScore Score(
        double distanceMeters, IssueCategory observationCategory, IssueCategory incidentCategory,
        double visualSimilarity, double daysSinceLastObservation, ScoringOptions o)
    {
        // Quadratic falloff: forgiving GPS jitter near the pin, sharp cut-off at the radius edge.
        var spatial = distanceMeters >= o.MatchRadiusMeters ? 0 : 1 - Math.Pow(distanceMeters / o.MatchRadiusMeters, 2);
        var category = CategoryRules.Similarity(observationCategory, incidentCategory);
        var visual = Math.Clamp(visualSimilarity, 0, 1);
        var temporal = Math.Clamp(1 - Math.Max(0, daysSinceLastObservation) / 90.0, 0, 1);

        var total = o.WeightSpatial * spatial + o.WeightCategory * category + o.WeightVisual * visual + o.WeightTemporal * temporal;
        total = Math.Round(Math.Clamp(total, 0, 1), 4);

        var verdict = total >= o.VeryLikelyThreshold ? MatchVerdict.VeryLikelySame
            : total >= o.PossibleThreshold ? MatchVerdict.PossibleMatch
            : MatchVerdict.LikelyNew;

        var reason = $"{distanceMeters:0} m away, {(category >= 1 ? "same category" : category > 0 ? "related category" : "different category")}, " +
                     $"visual similarity {visual:P0}, last seen {daysSinceLastObservation:0.#} days ago";

        return new SameIncidentScore(total, spatial, category, visual, temporal, verdict, distanceMeters, reason);
    }
}
