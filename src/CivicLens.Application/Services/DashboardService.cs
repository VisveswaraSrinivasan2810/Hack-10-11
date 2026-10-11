using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Application.Dtos;
using CivicLens.Domain;
using CivicLens.Domain.Configuration;
using CivicLens.Domain.Entities;
using CivicLens.Domain.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CivicLens.Application.Services;

/// <summary>City intelligence: summary KPIs, hotspots, recurrence, zone health and analytics.</summary>
public sealed class DashboardService(
    IAppDbContext db, IncidentService incidents, ResolutionService resolution, IncidentLifecycle lifecycle, IOptions<ScoringOptions> options)
{
    private readonly ScoringOptions _o = options.Value;

    public async Task<DashboardSummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        var now = lifecycle.Now;
        // Scales with open work, not with history: active incidents + a rolling 180-day window of resolutions, totals via COUNT.
        var active = await db.Incidents.AsNoTracking()
            .Where(i => i.Status != IncidentStatus.Rejected && i.Status != IncidentStatus.VerifiedResolved).ToListAsync(ct);
        var since = now.AddDays(-180);
        var resolved = await db.Incidents.AsNoTracking()
            .Where(i => i.Status == IncidentStatus.VerifiedResolved && i.ResolvedAt != null && i.ResolvedAt >= since)
            .Select(i => new { ResolvedAt = i.ResolvedAt!.Value, i.FirstObservedAt }).ToListAsync(ct);
        var totalIncidents = await db.Incidents.CountAsync(i => i.Status != IncidentStatus.Rejected, ct);
        var avgHours = resolved.Count == 0 ? 0 : resolved.Average(i => (i.ResolvedAt - i.FirstObservedAt).TotalHours);
        var health = await CityHealthAsync(ct);
        var observations = await db.Observations.CountAsync(ct);
        var hotspots = HotspotDetector.Detect(ToPoints(active), now, _o);

        return new DashboardSummaryDto(
            active.Count, active.Count(i => i.PriorityBand == PriorityBand.Critical), active.Count(i => i.PriorityScore >= 50),
            resolved.Count(i => i.ResolvedAt.Date == now.Date), active.Count(i => i.RecurrencePriorCount >= _o.RecurringMinPriorIncidents),
            active.Count(i => i.ResolutionStatus == ResolutionStatus.Disputed), active.Count(i => i.ResolutionStatus == ResolutionStatus.AwaitingVerification),
            hotspots.Count(h => h.IsEmerging), Math.Round(avgHours, 1), health, HealthScoreCalculator.Label(HealthScoreCalculator.BandFor(health)),
            observations, totalIncidents, totalIncidents == 0 ? 0 : Math.Round(observations / (double)totalIncidents, 2));
    }

    public async Task<IReadOnlyList<HotspotDto>> HotspotsAsync(CancellationToken ct = default)
    {
        var active = await db.Incidents.AsNoTracking().Where(i => i.Status != IncidentStatus.Rejected && i.Status != IncidentStatus.VerifiedResolved).ToListAsync(ct);
        var hotspots = HotspotDetector.Detect(ToPoints(active), lifecycle.Now, _o);
        var pois = await db.PointsOfInterest.AsNoTracking().ToListAsync(ct);
        var zones = await db.Zones.AsNoTracking().ToListAsync(ct);

        return hotspots.Select((h, idx) =>
        {
            var nearest = pois.Select(p => (p, d: GeoMath.DistanceMeters(h.CenterLatitude, h.CenterLongitude, p.Latitude, p.Longitude)))
                .OrderBy(x => x.d).FirstOrDefault();
            var zone = zones.FirstOrDefault(z => z.Boundary.Contains(GeoMath.ToPoint(h.CenterLatitude, h.CenterLongitude)));
            var name = nearest.p is not null && nearest.d <= 400 ? nearest.p.Name : zone?.Name ?? "Unnamed area";
            var title = h.IsEmerging ? "EMERGING HOTSPOT" : h.RecurringCount >= 2 ? "RECURRING HOTSPOT" : h.CriticalCount > 0 ? "CRITICAL CLUSTER" : "ACTIVE HOTSPOT";
            return new HotspotDto(idx + 1, name, h.CenterLatitude, h.CenterLongitude, (int)h.RadiusMeters, h.IncidentCount, h.CriticalCount,
                h.RecurringCount, h.CategoryCounts.ToDictionary(k => CategoryRules.Label(k.Key), k => k.Value),
                h.Last14DaysByCategory.ToDictionary(k => CategoryRules.Label(k.Key), k => k.Value), h.Last14Days, h.Previous14Days,
                h.TrendPercent, CpsCalculator.BandLabel(h.Priority), h.PriorityScore, h.IsEmerging, title, h.IncidentIds);
        }).ToList();
    }

    /// <summary>Locations (50 m clusters, same category family) with repeated incidents within the look-back window.</summary>
    public async Task<IReadOnlyList<RecurringLocationDto>> RecurringAsync(CancellationToken ct = default)
    {
        var cutoff = lifecycle.Now.AddDays(-_o.RecurrenceWindowDays);
        var all = await db.Incidents.AsNoTracking().Where(i => i.Status != IncidentStatus.Rejected && i.FirstObservedAt >= cutoff)
            .OrderBy(i => i.FirstObservedAt).ToListAsync(ct);

        var clusters = new List<(double Lat, double Lon, string Family, List<CivicIncident> Items)>();
        foreach (var i in all)
        {
            var fam = CategoryRules.Family(i.Category);
            var idx = clusters.FindIndex(c => c.Family == fam && GeoMath.DistanceMeters(c.Lat, c.Lon, i.Latitude, i.Longitude) <= _o.RecurrenceRadiusMeters);
            if (idx < 0) clusters.Add((i.Latitude, i.Longitude, fam, [i]));
            else clusters[idx].Items.Add(i);
        }

        return clusters.Where(c => c.Items.Count >= _o.RecurringMinPriorIncidents + 1)
            .OrderByDescending(c => c.Items.Count)
            .Select(c =>
            {
                var latest = c.Items.Last();
                var risk = c.Items.Count - 1 >= 3 ? "HIGH" : c.Items.Count - 1 == 2 ? "MEDIUM" : "LOW";
                return new RecurringLocationDto(
                    latest.LocationLabel ?? $"{c.Lat:0.0000}, {c.Lon:0.0000}", c.Lat, c.Lon, c.Items.Count, risk,
                    c.Items.Select(x => CategoryRules.Label(x.Category)).Distinct().ToList(),
                    c.Items.Select(x => new MemoryItemDto(x.Id, x.PublicIncidentCode, x.Category.ToString(), CategoryRules.Label(x.Category),
                        x.FirstObservedAt, x.Status.ToString(), x.Id == latest.Id)).ToList(),
                    c.Items.Any(x => x.IsActive));
            }).ToList();
    }

    public async Task<IReadOnlyList<ResolutionQueueItemDto>> DisputedAsync(CancellationToken ct = default) =>
        await QueueAsync(i => i.ResolutionStatus == ResolutionStatus.Disputed && i.Status != IncidentStatus.Rejected, ct);

    public async Task<IReadOnlyList<ResolutionQueueItemDto>> VerificationQueueAsync(CancellationToken ct = default) =>
        await QueueAsync(i => i.Status == IncidentStatus.AwaitingVerification || i.Status == IncidentStatus.WorkCompleted, ct);

    private async Task<IReadOnlyList<ResolutionQueueItemDto>> QueueAsync(System.Linq.Expressions.Expression<Func<CivicIncident, bool>> filter, CancellationToken ct)
    {
        var list = await db.Incidents.AsNoTracking().Where(filter).OrderByDescending(i => i.PriorityScore).ToListAsync(ct);
        var summaries = await incidents.SummariesAsync(list, ct);
        var result = new List<ResolutionQueueItemDto>();
        foreach (var s in summaries) result.Add(new ResolutionQueueItemDto(s, await resolution.BuildDtoAsync(s.Id, ct)));
        return result;
    }

    // ---------------------------------------------------------------- zones
    public async Task<IReadOnlyList<ZoneDto>> ZonesAsync(CancellationToken ct = default)
    {
        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.Name).ToListAsync(ct);
        var result = new List<ZoneDto>();
        foreach (var z in zones)
        {
            var h = await ZoneHealthAsync(z.Id, ct);
            result.Add(new ZoneDto(z.Id, z.Name, z.CenterLatitude, z.CenterLongitude, h.Score, h.Label,
                z.Boundary.ExteriorRing.Coordinates.Select(c => new LatLonDto(c.Y, c.X)).ToList()));
        }
        return result;
    }

    public async Task<ZoneHealthDto> ZoneHealthAsync(Guid zoneId, CancellationToken ct = default)
    {
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == zoneId, ct) ?? throw new NotFoundException("Zone not found.");
        var since = lifecycle.Now.AddDays(-90);
        var items = await db.Incidents.AsNoTracking()
            .Where(i => i.ZoneId == zoneId && i.Status != IncidentStatus.Rejected && (i.Status != IncidentStatus.VerifiedResolved || i.ResolvedAt >= since)).ToListAsync(ct);
        var stats = StatsFor(items);
        var h = HealthScoreCalculator.Calculate(stats);
        if (Math.Abs(zone.CivicHealthScore - h.Score) > 0.01) { zone.CivicHealthScore = h.Score; await db.SaveChangesAsync(ct); }
        return new ZoneHealthDto(zone.Id, zone.Name, h.Score, HealthScoreCalculator.Label(h.Band), stats.ActiveIncidents, stats.CriticalIncidents,
            stats.RecurringIncidents, stats.DisputedResolutions, Math.Round(stats.AverageResolutionHours, 1), h.Penalties);
    }

    private async Task<double> CityHealthAsync(CancellationToken ct)
    {
        var zones = await db.Zones.AsNoTracking().Select(z => z.Id).ToListAsync(ct);
        if (zones.Count == 0)
            return HealthScoreCalculator.Calculate(StatsFor(await db.Incidents.AsNoTracking().Where(i => i.Status != IncidentStatus.Rejected).ToListAsync(ct))).Score;
        var scores = new List<double>();
        foreach (var id in zones) scores.Add((await ZoneHealthAsync(id, ct)).Score);
        return Math.Round(scores.Average(), 1);
    }

    private ZoneStats StatsFor(IReadOnlyList<CivicIncident> items)
    {
        var active = items.Where(i => i.IsActive).ToList();
        var resolved = items.Where(i => i.Status == IncidentStatus.VerifiedResolved && i.ResolvedAt != null).ToList();
        return new ZoneStats(
            active.Count, active.Count(i => i.PriorityBand == PriorityBand.Critical),
            active.Count(i => i.RecurrencePriorCount >= _o.RecurringMinPriorIncidents),
            active.Count(i => i.ResolutionStatus == ResolutionStatus.Disputed),
            resolved.Count == 0 ? 0 : resolved.Average(i => (i.ResolvedAt!.Value - i.FirstObservedAt).TotalHours));
    }

    // ---------------------------------------------------------------- analytics
    public async Task<AnalyticsDto> AnalyticsAsync(CancellationToken ct = default)
    {
        var now = lifecycle.Now;
        var yearAgo = now.AddDays(-365);
        var all = await db.Incidents.AsNoTracking()
            .Where(i => i.Status != IncidentStatus.Rejected && (i.FirstObservedAt >= yearAgo || (i.Status != IncidentStatus.VerifiedResolved))).ToListAsync(ct);

        var byCategory = all.GroupBy(i => i.Category).OrderByDescending(g => g.Count())
            .Select(g => new ChartPoint(CategoryRules.Label(g.Key), g.Count())).ToList();
        var byStatus = all.GroupBy(i => i.Status).OrderBy(g => g.Key)
            .Select(g => new ChartPoint(g.Key.ToString(), g.Count())).ToList();
        var avgRes = all.Where(i => i.Status == IncidentStatus.VerifiedResolved && i.ResolvedAt != null).GroupBy(i => i.Category)
            .Select(g => new ChartPoint(CategoryRules.Label(g.Key), Math.Round(g.Average(i => (i.ResolvedAt!.Value - i.FirstObservedAt).TotalHours), 1))).ToList();

        var trend = new List<ChartPoint>();
        for (var m = 11; m >= 0; m--)
        {
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-m);
            var end = start.AddMonths(1);
            trend.Add(new ChartPoint(start.ToString("MMM yy"), all.Count(i => i.FirstObservedAt >= start && i.FirstObservedAt < end && i.RecurrencePriorCount >= 1)));
        }

        var zoneHealth = new List<ChartPoint>();
        foreach (var z in await db.Zones.AsNoTracking().OrderBy(z => z.Name).ToListAsync(ct))
            zoneHealth.Add(new ChartPoint(z.Name, (await ZoneHealthAsync(z.Id, ct)).Score));

        var active = all.Where(i => i.IsActive).ToList();
        var dist = Enum.GetValues<PriorityBand>().Select(b => new ChartPoint(CpsCalculator.BandLabel(b), active.Count(i => i.PriorityBand == b))).ToList();

        return new AnalyticsDto(byCategory, byStatus, avgRes, trend, zoneHealth, dist);
    }

    public async Task<IReadOnlyList<PoiDto>> PoisAsync(CancellationToken ct = default) =>
        await db.PointsOfInterest.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new PoiDto(p.Id, p.Name, p.Type.ToString(), p.Latitude, p.Longitude)).ToListAsync(ct);

    private static IReadOnlyList<HotspotPoint> ToPoints(IEnumerable<CivicIncident> items) =>
        items.Select(i => new HotspotPoint(i.Id, i.Latitude, i.Longitude, i.Category, i.PriorityScore, i.RecurrencePriorCount >= 2, i.FirstObservedAt)).ToList();
}
