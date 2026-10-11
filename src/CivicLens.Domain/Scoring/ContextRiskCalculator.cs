namespace CivicLens.Domain.Scoring;

public sealed record PoiHit(string Name, PoiType Type, double RiskWeight, double DistanceMeters);

public sealed record ContextRiskResult(double LocationRisk, double Exposure, IReadOnlyList<string> Reasons, IReadOnlyList<PoiHit> NearbyPois);

/// <summary>
/// Contextual risk: the same pothole is more urgent next to a school than on a quiet street.
/// LocationRisk = max(30, strongest POI contribution + 0.2 * second strongest).
/// </summary>
public static class ContextRiskCalculator
{
    public const double BaselineRisk = 30;

    /// <summary>Influence radius per POI type (metres) and how much footfall it implies.</summary>
    public static (double Radius, double Exposure, string Noun) Profile(PoiType t) => t switch
    {
        PoiType.School => (200, 90, "school"),
        PoiType.Hospital => (250, 85, "hospital"),
        PoiType.BusStop => (100, 70, "bus stop"),
        PoiType.Junction => (120, 80, "busy junction"),
        PoiType.Market => (150, 85, "market"),
        _ => (100, 50, "public place")
    };

    public static double MaxSearchRadius => Enum.GetValues<PoiType>().Max(t => Profile(t).Radius);

    public static ContextRiskResult Calculate(IssueCategory category, IEnumerable<PoiHit> pois)
    {
        var contributions = new List<(PoiHit Poi, double Risk, double Exposure)>();
        foreach (var p in pois)
        {
            var (radius, expo, _) = Profile(p.Type);
            if (p.DistanceMeters >= radius) continue;
            var proximity = 1 - Math.Pow(p.DistanceMeters / radius, 2);
            contributions.Add((p, p.RiskWeight * proximity, expo * proximity));
        }

        var byRisk = contributions.OrderByDescending(c => c.Risk).ToList();
        var top = byRisk.Count > 0 ? byRisk[0].Risk : 0;
        var second = byRisk.Count > 1 ? byRisk[1].Risk : 0;
        var locationRisk = Math.Round(Math.Clamp(Math.Max(BaselineRisk, top) + 0.2 * second, 0, 100), 1);

        var topExposure = contributions.Count > 0 ? contributions.Max(c => c.Exposure) : 0;
        var exposure = Math.Round(Math.Clamp(CategoryRules.BaseExposure(category) + 0.6 * topExposure, 0, 100), 1);

        var reasons = new List<string>();
        foreach (var c in byRisk.Take(3))
        {
            var noun = Profile(c.Poi.Type).Noun;
            reasons.Add($"Priority increased because this incident is {c.Poi.DistanceMeters:0} m from a {noun} ({c.Poi.Name})");
        }

        return new ContextRiskResult(locationRisk, exposure, reasons, byRisk.Select(c => c.Poi).ToList());
    }

    public static string RiskLabel(double locationRisk) => locationRisk >= 75 ? "HIGH" : locationRisk >= 50 ? "ELEVATED" : "NORMAL";
}
