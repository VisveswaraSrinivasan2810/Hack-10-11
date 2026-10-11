using System.Net;
using CivicLens.Application.Demo;
using CivicLens.Application.Dtos;

namespace CivicLens.Tests.Integration;

/// <summary>Read-only checks of the seeded demo city. Runs on its own database so other workflows cannot change the numbers.</summary>
public class SeedStateTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // ===================================================================== seed
    [Fact]
    public async Task Seed_data_matches_the_specified_demo_city()
    {
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var summary = await authority.GetAsync<DashboardSummaryDto>("/api/dashboard/summary");
        var all = await authority.GetAsync<PagedResult<IncidentSummaryDto>>("/api/incidents?pageSize=200");

        Assert.Equal(15, summary.ActiveIncidents);
        Assert.Equal(10, all.Items.Count(i => i.Status == "VerifiedResolved"));
        Assert.True(summary.CriticalIncidents >= 3, $"critical={summary.CriticalIncidents}");
        Assert.Equal(2, summary.DisputedResolutions);
        Assert.True(summary.EmergingHotspots >= 1);
        Assert.True((await authority.GetAsync<List<RecurringLocationDto>>("/api/dashboard/recurring")).Count >= 3);
        Assert.True((await authority.GetAsync<List<PoiDto>>("/api/pois")).Count >= 4);
        Assert.Equal(3, (await authority.GetAsync<List<ZoneDto>>("/api/zones")).Count);
        Assert.True((await authority.GetAsync<List<CorrelationDto>>("/api/dashboard/correlations")).Count >= 1);
        Assert.True((await authority.GetAsync<List<HotspotDto>>("/api/dashboard/hotspots")).Count >= 1);
        Assert.True(summary.CivicHealthScore is > 0 and < 100);
        Assert.Equal(2, (await authority.GetAsync<List<ResolutionQueueItemDto>>("/api/dashboard/disputed")).Count);

        foreach (var email in new[] { DemoCity.CitizenEmail, DemoCity.WorkerEmail })
            await factory.LoginAsync(email);
    }

    // ===================================================================== dashboard intelligence
    [Fact]
    public async Task Dashboard_exposes_hotspots_recurring_locations_health_and_analytics()
    {
        var a = await factory.LoginAsync(DemoCity.AuthorityEmail);

        var hotspot = (await a.GetAsync<List<HotspotDto>>("/api/dashboard/hotspots"))[0];
        Assert.True(hotspot.IncidentCount >= 6);
        Assert.Equal("Demo Junction", hotspot.Name);
        Assert.Contains("HOTSPOT", hotspot.Title);

        var recurring = await a.GetAsync<List<RecurringLocationDto>>("/api/dashboard/recurring");
        Assert.All(recurring, r => Assert.True(r.IncidentCount >= 3));
        Assert.Contains(recurring, r => r.Name.Contains("Central Avenue") && r.HasActiveIncident);

        var zones = await a.GetAsync<List<ZoneDto>>("/api/zones");
        Assert.All(zones, z => { Assert.InRange(z.HealthScore, 0, 100); Assert.True(z.Boundary.Count >= 4); });
        Assert.True(zones.Max(z => z.HealthScore) > zones.Min(z => z.HealthScore));
        var health = await a.GetAsync<ZoneHealthDto>($"/api/zones/{zones[0].Id}/health");
        Assert.NotNull(health.Penalties);

        var analytics = await a.GetAsync<AnalyticsDto>("/api/dashboard/analytics");
        Assert.NotEmpty(analytics.ByCategory);
        Assert.NotEmpty(analytics.ByStatus);
        Assert.Equal(12, analytics.RecurrenceTrend.Count);
        Assert.Equal(3, analytics.ZoneHealth.Count);
        Assert.Equal(5, analytics.CpsDistribution.Count);

        var corr = (await a.GetAsync<List<CorrelationDto>>("/api/dashboard/correlations")).First();
        Assert.Contains("may have contributed", corr.Explanation);
        Assert.Contains("potential relationship", corr.Explanation);

        var memory = await a.GetAsync<List<MemoryItemDto>>($"/api/memory?lat={DemoCity.RecurringXLat}&lon={DemoCity.RecurringXLon}&radius=75");
        Assert.Equal(3, memory.Count);
    }

    [Fact]
    public async Task Priority_queue_sorts_by_cps_and_filters_work()
    {
        var a = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var q = await a.GetAsync<PagedResult<IncidentSummaryDto>>("/api/incidents?activeOnly=true&pageSize=100");
        Assert.Equal(q.Items.OrderByDescending(i => i.PriorityScore).Select(i => i.Id), q.Items.Select(i => i.Id));
        var critical = await a.GetAsync<PagedResult<IncidentSummaryDto>>("/api/incidents?band=Critical&activeOnly=true");
        Assert.All(critical.Items, i => Assert.Equal("CRITICAL", i.PriorityBand));
        var potholes = await a.GetAsync<PagedResult<IncidentSummaryDto>>("/api/incidents?category=Pothole");
        Assert.All(potholes.Items, i => Assert.Equal("Pothole", i.Category));
        await a.ExpectErrorAsync(() => a.GetAsync<PagedResult<IncidentSummaryDto>>("/api/incidents?status=Nope"), HttpStatusCode.BadRequest);

        var nearby = await (await factory.LoginAsync(DemoCity.CitizenEmail)).GetAsync<List<IncidentSummaryDto>>($"/api/incidents/nearby?lat={DemoCity.HotspotLat}&lon={DemoCity.HotspotLon}&radius=300");
        Assert.True(nearby.Count >= 6);
        Assert.Equal(nearby.OrderBy(n => n.DistanceMeters).Select(n => n.Id), nearby.Select(n => n.Id));
    }

}
