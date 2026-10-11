using System.Net;
using CivicLens.Application.Demo;
using CivicLens.Application.Dtos;

namespace CivicLens.Tests.Integration;

/// <summary>Demo scenarios mutate shared state (they create incidents at fixed demo spots), so they get their own database.</summary>
public class DemoScenarioTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task All_ten_demo_scenarios_run_and_demonstrate_their_concept()
    {
        var a = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var list = await a.GetAsync<List<DemoScenarioDto>>("/api/demo/scenarios");
        Assert.Equal(10, list.Count);

        var results = new Dictionary<int, DemoScenarioResult>();
        foreach (var s in list) results[s.Id] = await a.PostAsync<DemoScenarioResult>($"/api/demo/scenarios/{s.Id}/run");
        Assert.All(results.Values, r => Assert.NotNull(r.IncidentId));

        Assert.Contains(results[2].Highlights, h => h.Contains("Citizen observations 1 → 2"));
        Assert.Contains(results[3].Highlights, h => h.Contains("from a school"));
        Assert.Contains("RECURRING INFRASTRUCTURE ISSUE", results[6].Highlights);
        Assert.Equal("VERIFIED RESOLVED", results[7].Summary);
        Assert.Contains("DISPUTED", results[8].Summary);
        Assert.Contains(results[9].Highlights, h => h.Contains("may have contributed"));

        var d7 = await a.GetAsync<IncidentDetailDto>($"/api/incidents/{results[7].IncidentId}");
        Assert.Equal("VerifiedResolved", d7.Summary.Status);
        var d8 = await a.GetAsync<IncidentDetailDto>($"/api/incidents/{results[8].IncidentId}");
        Assert.Equal("RequiresReview", d8.Summary.Status);
        var d3 = await a.GetAsync<IncidentDetailDto>($"/api/incidents/{results[3].IncidentId}");
        Assert.Equal("CRITICAL", d3.Priority.Band);
    }

    [Fact]
    public async Task Demo_endpoints_are_authority_only_and_reset_restores_seed_state()
    {
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        await citizen.ExpectErrorAsync(() => citizen.GetAsync<List<DemoScenarioDto>>("/api/demo/scenarios"), HttpStatusCode.Forbidden);

        var a = await factory.LoginAsync(DemoCity.AuthorityEmail);
        await a.ExpectErrorAsync(() => a.PostAsync<DemoScenarioResult>("/api/demo/scenarios/99/run"), HttpStatusCode.NotFound);
        var info = await factory.Client().GetAsync<System.Text.Json.JsonElement>("/api/demo/info");
        Assert.True(info.GetProperty("demoMode").GetBoolean());
    }
}
