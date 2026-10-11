using CivicLens.Domain.Configuration;

namespace CivicLens.Domain.Scoring;

public sealed record HotspotPoint(
    Guid Id, double Latitude, double Longitude, IssueCategory Category, double Priority, bool IsRecurring, DateTime FirstObservedAt);

public sealed record Hotspot(
    double CenterLatitude, double CenterLongitude, double RadiusMeters, int IncidentCount,
    int CriticalCount, int RecurringCount, IReadOnlyDictionary<IssueCategory, int> CategoryCounts,
    IReadOnlyDictionary<IssueCategory, int> Last14DaysByCategory, int Last14Days, int Previous14Days, double TrendPercent,
    double PriorityScore, PriorityBand Priority, bool IsEmerging, IReadOnlyList<Guid> IncidentIds);

/// <summary>
/// Density-based clustering of active incidents (single-linkage, eps = HotspotRadiusMeters, minPts = HotspotMinIncidents).
/// Equivalent to PostGIS ST_ClusterDBSCAN but runs in-process so it is testable and DB-agnostic.
/// </summary>
public static class HotspotDetector
{
    public static IReadOnlyList<Hotspot> Detect(IReadOnlyList<HotspotPoint> points, DateTime now, ScoringOptions o)
    {
        var n = points.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (GeoMath.DistanceMeters(points[i].Latitude, points[i].Longitude, points[j].Latitude, points[j].Longitude) <= o.HotspotRadiusMeters)
                    parent[Find(i)] = Find(j);

        var result = new List<Hotspot>();
        foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
        {
            var members = group.Select(i => points[i]).ToList();
            if (members.Count < o.HotspotMinIncidents) continue;

            var lat = members.Average(m => m.Latitude);
            var lon = members.Average(m => m.Longitude);
            var radius = Math.Max(30, members.Max(m => GeoMath.DistanceMeters(lat, lon, m.Latitude, m.Longitude)));

            var recent = members.Where(m => m.FirstObservedAt >= now.AddDays(-14)).ToList();
            var previous = members.Count(m => m.FirstObservedAt >= now.AddDays(-28) && m.FirstObservedAt < now.AddDays(-14));
            var trend = Math.Round((recent.Count - previous) / (double)Math.Max(1, previous) * 100, 0);

            var avg = members.Average(m => m.Priority);
            var score = Math.Round(Math.Clamp(avg * 0.6 + Math.Min(members.Count, 10) * 4, 0, 100), 1);

            result.Add(new Hotspot(
                lat, lon, Math.Round(radius), members.Count,
                members.Count(m => m.Priority >= 85), members.Count(m => m.IsRecurring),
                members.GroupBy(m => m.Category).ToDictionary(g => g.Key, g => g.Count()),
                recent.GroupBy(m => m.Category).ToDictionary(g => g.Key, g => g.Count()),
                recent.Count, previous, trend, score, CpsCalculator.BandFor(score),
                recent.Count >= o.HotspotMinIncidents && trend > 0,
                members.Select(m => m.Id).ToList()));
        }
        return result.OrderByDescending(h => h.PriorityScore).ToList();
    }
}
