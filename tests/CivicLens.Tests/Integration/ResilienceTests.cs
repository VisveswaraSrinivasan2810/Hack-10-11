using System.Net;
using CivicLens.Application.Abstractions;
using CivicLens.Application.Demo;
using CivicLens.Application.Dtos;
using CivicLens.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CivicLens.Tests.Integration;

/// <summary>Spec §38/§39: the demo must survive AI outages. These tests replace the AI providers with ones that always fail.</summary>
public class ResilienceTests
{
    private sealed class BrokenClassifier : IIssueClassificationService
    {
        public Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default) =>
            throw new HttpRequestException("AI service unreachable");
    }

    private sealed class BrokenVerifier : IResolutionVerificationService
    {
        public Task<ResolutionCheckResult> VerifyAsync(IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default) =>
            throw new HttpRequestException("AI service unreachable");
    }

    private sealed class BrokenAiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IIssueClassificationService>();
                s.RemoveAll<IResolutionVerificationService>();
                s.AddSingleton<IIssueClassificationService, BrokenClassifier>();
                s.AddSingleton<IResolutionVerificationService, BrokenVerifier>();
            });
        }
    }

    [Fact]
    public async Task Classification_outage_degrades_to_manual_category_selection_and_still_creates_the_incident()
    {
        await using var factory = new BrokenAiFactory();
        var c = await factory.LoginAsync(DemoCity.CitizenEmail);
        var a = await c.PostFormAsync<AnalysisResponse>("/api/observations/analyze",
            ApiClient.PhotoForm("pothole.jpg", DemoImages.Bytes("pothole"), 12.9500, 77.6300));

        Assert.True(a.IsLowConfidence);
        Assert.Equal("unavailable", a.Provider);
        Assert.Contains(a.Warnings, w => w.Contains("Automatic analysis is unavailable"));

        await c.ExpectErrorAsync(() => c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = a.DraftId, action = "CreateNew" }), HttpStatusCode.BadRequest);
        var r = await c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = a.DraftId, selectedCategory = "Pothole", action = "CreateNew" });
        Assert.True(r.IsNewIncident);
    }

    [Fact]
    public async Task Resolution_ai_outage_keeps_incident_awaiting_verification_and_authority_can_verify_manually_with_audit()
    {
        await using var factory = new BrokenAiFactory();
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var worker = await factory.LoginAsync(DemoCity.WorkerEmail);

        // the classifier is down too, so the citizen picks the category by hand
        var a = await citizen.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("garbage.jpg", DemoImages.Bytes("garbage"), 12.9520, 77.6300));
        var inc = await citizen.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = a.DraftId, selectedCategory = "GarbageAccumulation", action = "CreateNew" });
        var w = (await authority.GetAsync<List<WorkerDto>>("/api/users/workers")).First();
        await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = w.Id });
        await worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Accepted" });
        await worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "InProgress" });

        var res = await worker.PostFormAsync<ResolutionDto>($"/api/incidents/{inc.IncidentId}/resolution", ApiClient.AfterPhotoForm("after_cleaned.jpg"));
        Assert.Equal("Inconclusive", res.VerificationResult);
        Assert.Equal("Pending", res.Outcome);
        Assert.Contains("unavailable", res.AiReason);

        // Without a reason the manual override is refused; with one it is accepted and audited.
        await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "VerifiedResolved" }), HttpStatusCode.BadRequest);
        var done = await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "VerifiedResolved", reason = "Inspected on site" });
        Assert.Equal("VerifiedResolved", done.Status);
        var d = await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{inc.IncidentId}");
        Assert.Contains(d.Timeline, t => t.NewStatus == "VerifiedResolved" && t.Message.Contains("Manually verified") && t.Message.Contains("Inspected on site"));
    }

    [Fact]
    public async Task Authority_can_reassign_an_in_progress_incident_when_a_worker_is_unavailable()
    {
        await using var factory = new ApiFactory();
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var a = await citizen.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("pothole.jpg", DemoImages.Bytes("pothole"), 12.9540, 77.6300));
        var inc = await citizen.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = a.DraftId, action = "CreateNew" });
        var workers = await authority.GetAsync<List<WorkerDto>>("/api/users/workers");
        Assert.True(workers.Count >= 2);

        var first = workers.First(x => x.Name.StartsWith("Ravi"));
        var second = workers.First(x => x.Id != first.Id);
        var firstClient = await factory.LoginAsync(DemoCity.WorkerEmail);

        await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = first.Id });
        await firstClient.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Accepted" });
        await firstClient.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "InProgress" });

        var re = await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = second.Id, note = "Worker unavailable" });
        Assert.Equal("Assigned", re.Status);
        Assert.Equal(second.Id, re.AssignedWorkerId);
        var d = await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{inc.IncidentId}");
        Assert.Contains(d.Timeline, t => t.Message.Contains("Reassigned") && t.Message.Contains("Worker unavailable"));

        // the previous worker can no longer act on it
        await firstClient.ExpectErrorAsync(() => firstClient.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "InProgress" }), HttpStatusCode.Forbidden);
    }
}
