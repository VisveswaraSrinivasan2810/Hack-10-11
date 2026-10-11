using System.Net;
using CivicLens.Application.Demo;
using CivicLens.Application.Dtos;

namespace CivicLens.Tests.Integration;

/// <summary>Spec §45 integration tests: each test drives the real HTTP API end-to-end.</summary>
public class WorkflowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Spots far from seeded incidents/POIs and from each other (outside the three zones → baseline context).
    private static (double Lat, double Lon) Spot(int i) => (12.9500 + i * 0.0030, 77.6300);

    private static async Task<AnalysisResponse> AnalyzeAsync(ApiClient c, string file, (double Lat, double Lon) at, string? desc = null) =>
        await c.PostFormAsync<AnalysisResponse>("/api/observations/analyze",
            ApiClient.PhotoForm(file, DemoImages.Bytes(file), at.Lat, at.Lon, desc));

    private static Task<SubmitObservationResult> CreateNewAsync(ApiClient c, AnalysisResponse a, string? category = null) =>
        c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = a.DraftId, selectedCategory = category, action = "CreateNew" });

    private static Task<SubmitObservationResult> AttachAsync(ApiClient c, AnalysisResponse a, Guid incidentId) =>
        c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = a.DraftId, action = "AttachToExisting", incidentId });

    // ===================================================================== observation → incident
    [Fact]
    public async Task Observation_creates_incident_with_ai_context_cps_and_audit_trail()
    {
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var a = await AnalyzeAsync(citizen, "pothole_a.jpg", Spot(1));

        Assert.Equal("Pothole", a.DetectedCategory);
        Assert.InRange(a.ConfidencePercent, 90, 100);
        Assert.False(a.IsLowConfidence);
        Assert.Empty(a.Matches);

        var r = await CreateNewAsync(citizen, a);
        Assert.True(r.IsNewIncident);
        Assert.StartsWith("CL-", r.Code);
        Assert.Equal(1, r.ObservationCountAfter);
        Assert.Equal("Reported", r.Status);
        Assert.True(r.PriorityAfter > 0);

        var d = await citizen.GetAsync<IncidentDetailDto>($"/api/incidents/{r.IncidentId}");
        Assert.Equal(r.PriorityAfter, d.Priority.FinalScore);
        Assert.NotEmpty(d.Priority.Reasons);
        Assert.Contains(d.Timeline, t => t.EventType == "ObservationSubmitted");
        Assert.Contains(d.Timeline, t => t.EventType == "AiClassified");
        Assert.Contains(d.Timeline, t => t.EventType == "IncidentCreated");
        Assert.Single(d.Observations);
        Assert.True(d.Observations[0].IsMine);

        // Authority sees it immediately.
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var list = await authority.GetAsync<PagedResult<IncidentSummaryDto>>("/api/incidents?search=" + r.Code);
        Assert.Contains(list.Items, i => i.Id == r.IncidentId);
    }

    [Fact]
    public async Task School_school_road_pothole_reports_72m_distance_and_high_location_risk()
    {
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var a = await AnalyzeAsync(citizen, "pothole_school_road.jpg", (DemoCity.SchoolRoadLat, DemoCity.SchoolRoadLon));

        Assert.True(a.Context.LocationRisk >= 75, $"risk={a.Context.LocationRisk}");
        Assert.Equal("HIGH", a.Context.LocationRiskLabel);
        Assert.Contains(a.Context.Reasons, x => x.Contains("72 m from a school"));
        Assert.Contains(a.Context.NearbyPois, p => p.Type == "School" && p.DistanceMeters is >= 71 and <= 73);
        Assert.Equal("Near Demo Public School", a.LocationLabel);
    }

    // ===================================================================== observation → existing incident
    [Fact]
    public async Task Second_citizen_gets_possible_existing_incident_and_merge_raises_priority()
    {
        var one = await factory.LoginAsync(DemoCity.CitizenEmail);
        var a1 = await AnalyzeAsync(one, "pothole_first.jpg", Spot(2));
        var first = await CreateNewAsync(one, a1);

        var two = await factory.LoginAsync(DemoCity.CitizenTwoEmail);
        var a2 = await AnalyzeAsync(two, "pothole_second.jpg", (Spot(2).Lat + 0.0001, Spot(2).Lon));

        var match = Assert.Single(a2.Matches);
        Assert.Equal(first.IncidentId, match.IncidentId);
        Assert.Equal("VeryLikelySame", match.Verdict);
        Assert.InRange(match.SameIncidentPercent, 85, 100);
        Assert.InRange(match.DistanceMeters, 9, 13);

        var merged = await AttachAsync(two, a2, first.IncidentId);
        Assert.False(merged.IsNewIncident);
        Assert.Equal(1, merged.ObservationCountBefore);
        Assert.Equal(2, merged.ObservationCountAfter);
        Assert.True(merged.PriorityAfter > merged.PriorityBefore, $"{merged.PriorityBefore} → {merged.PriorityAfter}");

        var detail = await two.GetAsync<IncidentDetailDto>($"/api/incidents/{first.IncidentId}");
        Assert.Equal(2, detail.Observations.Count);
        Assert.Contains(detail.Timeline, t => t.EventType == "ObservationMerged");
        Assert.Contains(detail.Timeline, t => t.EventType == "PriorityChanged" && t.Message.Contains("→"));
        // Privacy: public incident payload never exposes citizen identity.
        Assert.DoesNotContain("Asha", two.LastBody);
        Assert.DoesNotContain("citizen@civiclens.demo", two.LastBody);
    }

    [Fact]
    public async Task Citizen_can_override_a_likely_match_and_it_is_audited()
    {
        var one = await factory.LoginAsync(DemoCity.CitizenEmail);
        var first = await CreateNewAsync(one, await AnalyzeAsync(one, "pothole_x1.jpg", Spot(3)));

        var two = await factory.LoginAsync(DemoCity.CitizenTwoEmail);
        var a2 = await AnalyzeAsync(two, "pothole_x2.jpg", (Spot(3).Lat + 0.0001, Spot(3).Lon));
        Assert.NotEmpty(a2.Matches);
        var separate = await CreateNewAsync(two, a2);

        Assert.True(separate.IsNewIncident);
        Assert.True(separate.OverrodeLikelyMatch);
        Assert.NotEqual(first.IncidentId, separate.IncidentId);
        var d = await two.GetAsync<IncidentDetailDto>($"/api/incidents/{separate.IncidentId}");
        Assert.Contains(d.Timeline, t => t.EventType == "CitizenOverrodeMatch");
    }

    [Fact]
    public async Task Same_category_far_away_and_different_category_nearby_are_separate_incidents()
    {
        var c = await factory.LoginAsync(DemoCity.CitizenEmail);
        var first = await CreateNewAsync(c, await AnalyzeAsync(c, "pothole_y1.jpg", Spot(4)));

        var far = await AnalyzeAsync(c, "pothole_y2.jpg", (Spot(4).Lat + 0.0006, Spot(4).Lon)); // ~67 m
        Assert.Empty(far.Matches);

        var garbage = await AnalyzeAsync(c, "garbage_y3.jpg", Spot(4)); // same spot, different category
        Assert.Empty(garbage.Matches);
        Assert.NotEqual(first.IncidentId, (await CreateNewAsync(c, garbage)).IncidentId);
    }

    [Fact]
    public async Task Citizen_confirmation_adds_evidence_once_and_reporter_cannot_confirm_own_incident()
    {
        var one = await factory.LoginAsync(DemoCity.CitizenEmail);
        var inc = await CreateNewAsync(one, await AnalyzeAsync(one, "pothole_conf.jpg", Spot(5)));
        await one.ExpectErrorAsync(() => one.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/confirm"), HttpStatusCode.Conflict);

        var two = await factory.LoginAsync(DemoCity.CitizenTwoEmail);
        var s = await two.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/confirm");
        Assert.Equal(1, s.ConfirmationCount);
        Assert.True(s.PriorityScore > inc.PriorityAfter);
        await two.ExpectErrorAsync(() => two.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/confirm"), HttpStatusCode.Conflict);
    }

    // ===================================================================== incident → assignment → resolution → verification
    private async Task<(ApiClient Citizen, ApiClient Authority, ApiClient Worker, Guid Id, Guid WorkerId)> InProgressIncidentAsync(int spot, string photo = "garbage_incident.jpg")
    {
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var worker = await factory.LoginAsync(DemoCity.WorkerEmail);

        var inc = await CreateNewAsync(citizen, await AnalyzeAsync(citizen, photo, Spot(spot)));
        var workers = await authority.GetAsync<List<WorkerDto>>("/api/users/workers");
        var w = workers.First();
        var assigned = await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = w.Id, note = "Please prioritise" });
        Assert.Equal("Assigned", assigned.Status);
        Assert.Equal(w.Id, assigned.AssignedWorkerId);

        await worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Accepted" });
        var started = await worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "InProgress" });
        Assert.Equal("InProgress", started.Status);
        return (citizen, authority, worker, inc.IncidentId, w.Id);
    }

    [Fact]
    public async Task Assignment_resolution_ai_check_and_citizen_confirmation_end_in_verified_resolved()
    {
        var (citizen, authority, worker, id, _) = await InProgressIncidentAsync(6);

        var res = await worker.PostFormAsync<ResolutionDto>($"/api/incidents/{id}/resolution", ApiClient.AfterPhotoForm("garbage_fixed_after.jpg"));
        Assert.Equal("IssueNotDetected", res.VerificationResult);
        Assert.InRange(res.AiConfidencePercent, 90, 100);
        Assert.Equal("Pending", res.Outcome);                      // AI alone does not close the loop
        Assert.Contains("not detected", res.AfterSummary);

        var mid = await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{id}");
        Assert.Equal("AwaitingVerification", mid.Summary.Status);
        Assert.NotEqual("VerifiedResolved", mid.Summary.ResolutionStatus);
        Assert.True(mid.CanVerifyResolution is false);              // authority is not a citizen voter
        var queue = await authority.GetAsync<List<ResolutionQueueItemDto>>("/api/dashboard/verification-queue");
        Assert.Contains(queue, q => q.Summary.Id == id);

        var cd = await citizen.GetAsync<IncidentDetailDto>($"/api/incidents/{id}");
        Assert.True(cd.CanVerifyResolution);

        var verified = await citizen.PostAsync<ResolutionDto>($"/api/incidents/{id}/verify", new { vote = "Fixed" });
        Assert.Equal("Verified", verified.Outcome);
        Assert.Equal("VERIFIED RESOLVED", verified.OutcomeLabel);
        Assert.InRange(verified.ResolutionConfidencePercent, 85, 100);

        var final = await citizen.GetAsync<IncidentDetailDto>($"/api/incidents/{id}");
        Assert.Equal("VerifiedResolved", final.Summary.Status);
        Assert.Equal("VERIFIED RESOLVED", final.StatusBanner);
        Assert.Contains(final.Timeline, t => t.NewStatus == "VerifiedResolved");
        Assert.Contains(final.Timeline, t => t.NewStatus == "WorkCompleted");
        Assert.Contains(final.Timeline, t => t.NewStatus == "AwaitingVerification");

        var summary = await authority.GetAsync<DashboardSummaryDto>("/api/dashboard/summary");
        Assert.True(summary.ResolvedToday >= 1);

        // Trust grows through participation (resolution check) and verified observations.
        var trust = await citizen.GetAsync<TrustDto>("/api/users/me/trust");
        Assert.True(trust.ResolutionChecks >= 1);
        Assert.True(trust.VerifiedObservations >= 1);
    }

    [Fact]
    public async Task Ghost_closure_low_confidence_after_photo_is_disputed_and_never_verified()
    {
        var (citizen, authority, worker, id, _) = await InProgressIncidentAsync(7, "garbage_ghost.jpg");

        var res = await worker.PostFormAsync<ResolutionDto>($"/api/incidents/{id}/resolution",
            ApiClient.AfterPhotoForm("garbage_still_present_after.jpg", "Cleared and complete"));

        Assert.Equal("IssueStillPresent", res.VerificationResult);
        Assert.True(res.AiConfidencePercent < 50);
        Assert.Equal("Disputed", res.Outcome);
        Assert.Equal("RESOLUTION DISPUTED — REQUIRES REVIEW", res.OutcomeLabel);
        Assert.Contains("not an accusation", res.NeutralNote);       // neutral terminology, no accusation

        var d = await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{id}");
        Assert.Equal("RequiresReview", d.Summary.Status);
        Assert.Equal("Disputed", d.Summary.ResolutionStatus);
        Assert.Equal("RESOLUTION DISPUTED — REQUIRES REVIEW", d.StatusBanner);
        Assert.Contains(d.Timeline, t => t.NewStatus == "VerificationFailed");
        Assert.Contains(d.Timeline, t => t.NewStatus == "RequiresReview");
        Assert.DoesNotContain(d.Timeline, t => t.NewStatus == "VerifiedResolved");

        var disputed = await authority.GetAsync<List<ResolutionQueueItemDto>>("/api/dashboard/disputed");
        Assert.Contains(disputed, x => x.Summary.Id == id);

        // A single citizen "Fixed" vote cannot overturn failed physical evidence.
        var after = await citizen.PostAsync<ResolutionDto>($"/api/incidents/{id}/verify", new { vote = "Fixed" });
        Assert.Equal("Disputed", after.Outcome);
        Assert.Equal("RequiresReview", (await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{id}")).Summary.Status);

        // Authority resolves it with an audited, reasoned decision: reassign for rework.
        var workers = await authority.GetAsync<List<WorkerDto>>("/api/users/workers");
        var re = await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{id}/assign", new { workerId = workers[0].Id, note = "Rework required" });
        Assert.Equal("Assigned", re.Status);
    }

    [Fact]
    public async Task Citizens_reporting_still_exists_dispute_a_resolution_even_with_good_ai_score()
    {
        var (c1, authority, worker, id, _) = await InProgressIncidentAsync(8, "garbage_votes.jpg");

        // reporter + three confirmers corroborate while work is still open (confirming is closed once work is complete)
        var users = new[] { DemoCity.CitizenTwoEmail, "aarav@civiclens.demo", "bhavna@civiclens.demo" };
        foreach (var email in users)
        {
            var c = await factory.LoginAsync(email);
            await c.PostAsync<IncidentSummaryDto>($"/api/incidents/{id}/confirm");
        }
        await worker.PostFormAsync<ResolutionDto>($"/api/incidents/{id}/resolution", ApiClient.AfterPhotoForm("garbage_fixed_after.jpg"));

        ResolutionDto last = await c1.PostAsync<ResolutionDto>($"/api/incidents/{id}/verify", new { vote = "StillExists" });
        foreach (var email in users)
            last = await (await factory.LoginAsync(email)).PostAsync<ResolutionDto>($"/api/incidents/{id}/verify", new { vote = "StillExists" });

        Assert.Equal("Disputed", last.Outcome);
        Assert.Equal(4, last.Citizens.StillExists);
        Assert.Equal("RequiresReview", (await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{id}")).Summary.Status);
    }

    [Fact]
    public async Task Resolved_incident_that_receives_a_new_observation_is_reopened()
    {
        var (citizen, authority, worker, id, _) = await InProgressIncidentAsync(9, "garbage_reopen.jpg");
        await worker.PostFormAsync<ResolutionDto>($"/api/incidents/{id}/resolution", ApiClient.AfterPhotoForm("garbage_fixed_after.jpg"));
        await citizen.PostAsync<ResolutionDto>($"/api/incidents/{id}/verify", new { vote = "Fixed" });

        var two = await factory.LoginAsync(DemoCity.CitizenTwoEmail);
        var a = await AnalyzeAsync(two, "garbage_again.jpg", (Spot(9).Lat + 0.0001, Spot(9).Lon));
        var match = Assert.Single(a.Matches);
        Assert.True(match.IsResolved);

        var r = await AttachAsync(two, a, id);
        Assert.True(r.ReopenedIncident);
        Assert.Equal("Reopened", (await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{id}")).Summary.Status);
    }

    // ===================================================================== recurrence + infrastructure memory
    [Fact]
    public async Task Recurring_road_damage_at_location_x_shows_banner_four_related_incidents_and_raises_cps()
    {
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var a = await AnalyzeAsync(citizen, "road_damage_oct.jpg", (DemoCity.RecurringXLat, DemoCity.RecurringXLon));

        Assert.NotNull(a.Recurrence);
        Assert.True(a.Recurrence!.IsRecurring);
        Assert.Empty(a.Matches); // historical incidents are long resolved → candidates for recurrence, not duplicates

        var r = await CreateNewAsync(citizen, a);
        Assert.Equal("RECURRING INFRASTRUCTURE ISSUE", r.Recurrence!.Banner);
        Assert.Equal(4, r.Recurrence.RelatedTotal);
        Assert.Equal(3, r.Recurrence.PriorCount);
        Assert.Equal("HIGH", r.Recurrence.Risk);
        Assert.Contains("4 related incidents within 12 months", r.Recurrence.Summary);

        var d = await citizen.GetAsync<IncidentDetailDto>($"/api/incidents/{r.IncidentId}");
        Assert.True(d.Priority.Recurrence >= 80);
        Assert.Contains(d.Priority.Reasons, x => x.Contains("Recurring"));
        Assert.True(d.Memory.Count >= 4);                              // Infrastructure Memory timeline
        Assert.Contains(d.Memory, m => m.IsCurrent);
        Assert.Equal(new[] { "Pothole", "RoadDamage", "Pothole" }, d.Memory.Where(m => !m.IsCurrent).Select(m => m.Category));
        Assert.Equal(d.Memory.OrderBy(m => m.At).Select(m => m.Code), d.Memory.Select(m => m.Code)); // chronological
    }

    // ===================================================================== authorization + validation + failure paths
    [Fact]
    public async Task Authorization_is_enforced_for_every_role()
    {
        var anon = factory.Client();
        var ex = await anon.ExpectErrorAsync(() => anon.GetAsync<DashboardSummaryDto>("/api/dashboard/summary"), HttpStatusCode.Unauthorized);
        Assert.Equal("unauthorized", ex.Code);

        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var worker = await factory.LoginAsync(DemoCity.WorkerEmail);
        var inc = await CreateNewAsync(citizen, await AnalyzeAsync(citizen, "pothole_auth.jpg", Spot(10)));
        var workers = await authority.GetAsync<List<WorkerDto>>("/api/users/workers");

        // citizen cannot use authority/worker capabilities
        await citizen.ExpectErrorAsync(() => citizen.GetAsync<DashboardSummaryDto>("/api/dashboard/summary"), HttpStatusCode.Forbidden);
        await citizen.ExpectErrorAsync(() => citizen.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = workers[0].Id }), HttpStatusCode.Forbidden);
        await citizen.ExpectErrorAsync(() => citizen.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Validated" }), HttpStatusCode.Forbidden);
        await citizen.ExpectErrorAsync(() => citizen.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/override", new { severity = 10, reason = "x" }), HttpStatusCode.Forbidden);
        await citizen.ExpectErrorAsync(() => citizen.PostAsync<object>("/api/demo/reset"), HttpStatusCode.Forbidden);

        // authority cannot submit observations; worker cannot assign or see unassigned incidents
        await authority.ExpectErrorAsync(() => AnalyzeAsync(authority, "pothole_auth2.jpg", Spot(10)), HttpStatusCode.Forbidden);
        await worker.ExpectErrorAsync(() => worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = workers[0].Id }), HttpStatusCode.Forbidden);
        await worker.ExpectErrorAsync(() => worker.GetAsync<IncidentDetailDto>($"/api/incidents/{inc.IncidentId}"), HttpStatusCode.Forbidden);
        await worker.ExpectErrorAsync(() => worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Accepted" }), HttpStatusCode.Forbidden);
        var workerList = await worker.GetAsync<PagedResult<IncidentSummaryDto>>("/api/incidents?pageSize=200");
        Assert.DoesNotContain(workerList.Items, i => i.Id == inc.IncidentId);
        Assert.All(workerList.Items, i => Assert.Equal(workers[0].Id, i.AssignedWorkerId));

        // a worker assigned to an incident still cannot self-resolve or skip states
        await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = workers[0].Id });
        await worker.ExpectErrorAsync(() => worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "VerifiedResolved" }), HttpStatusCode.Conflict);
        await worker.ExpectErrorAsync(() => worker.PostFormAsync<ResolutionDto>($"/api/incidents/{inc.IncidentId}/resolution", ApiClient.AfterPhotoForm("fixed.jpg")), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Invalid_state_transitions_are_rejected_with_readable_errors()
    {
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var inc = await CreateNewAsync(citizen, await AnalyzeAsync(citizen, "pothole_state.jpg", Spot(11)));

        var ex = await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status",
            new { status = "VerifiedResolved", reason = "close it" }), HttpStatusCode.Conflict);
        Assert.Equal("invalid_transition", ex.Code);
        Assert.Contains("Reported", ex.Message);

        await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Banana" }), HttpStatusCode.BadRequest);
        await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Rejected" }), HttpStatusCode.BadRequest); // reason required
        await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Assigned" }), HttpStatusCode.BadRequest);

        var ok = await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Validated" });
        Assert.Equal("Validated", ok.Status);
        await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{Guid.NewGuid()}/status", new { status = "Validated" }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rejecting_an_incident_marks_observations_invalid_and_lowers_trust()
    {
        var citizen = await factory.LoginAsync("harsh@civiclens.demo");
        var before = (await citizen.GetAsync<TrustDto>("/api/users/me/trust")).Score;
        var inc = await CreateNewAsync(citizen, await AnalyzeAsync(citizen, "pothole_spam.jpg", Spot(12)));

        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Rejected", reason = "Not a civic issue" });

        var trust = await citizen.GetAsync<TrustDto>("/api/users/me/trust");
        Assert.Equal(1, trust.InvalidSubmissions);
        Assert.True(trust.Score < before, $"{before} → {trust.Score}");
        Assert.Contains((await citizen.GetAsync<List<NotificationDto>>("/api/users/me/notifications")), n => n.EventType == "observation_rejected");
    }

    [Fact]
    public async Task Upload_and_input_validation_failures_are_graceful()
    {
        var c = await factory.LoginAsync(DemoCity.CitizenTwoEmail);

        var html = System.Text.Encoding.ASCII.GetBytes("<html>definitely not an image</html>");
        var ex = await c.ExpectErrorAsync(() => c.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("evil.jpg", html, 12.97, 77.59)), HttpStatusCode.BadRequest);
        Assert.Equal("unsupported_image", ex.Code);

        var tooBig = DemoImages.Bytes("pothole").Concat(new byte[9 * 1024 * 1024]).ToArray();
        ex = await c.ExpectErrorAsync(() => c.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("big.jpg", tooBig, 12.97, 77.59)), HttpStatusCode.BadRequest);
        Assert.Equal("image_too_large", ex.Code);

        ex = await c.ExpectErrorAsync(() => c.PostFormAsync<AnalysisResponse>("/api/observations/analyze",
            new MultipartFormDataContent { { new StringContent("12.97"), "latitude" }, { new StringContent("77.59"), "longitude" } }), HttpStatusCode.BadRequest);
        Assert.Equal("image_required", ex.Code);

        ex = await c.ExpectErrorAsync(() => AnalyzeAsync(c, "pothole_bad.jpg", (95, 77.59)), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_location", ex.Code);
        ex = await c.ExpectErrorAsync(() => AnalyzeAsync(c, "pothole_bad.jpg", (40.7, -74.0)), HttpStatusCode.BadRequest);
        Assert.Equal("outside_service_area", ex.Code);

        ex = await c.ExpectErrorAsync(() => c.PostFormAsync<AnalysisResponse>("/api/observations/analyze",
            ApiClient.PhotoForm("pothole_far.jpg", DemoImages.Bytes("pothole"), 12.97, 77.59, deviceLat: 12.99, deviceLon: 77.59)), HttpStatusCode.BadRequest);
        Assert.Equal("location_mismatch", ex.Code);

        await c.ExpectErrorAsync(() => c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = Guid.NewGuid(), action = "CreateNew" }), HttpStatusCode.NotFound);
        await c.ExpectErrorAsync(() => c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = Guid.Empty, action = "" }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Same_photo_twice_is_rejected_and_reuse_by_another_citizen_is_flagged()
    {
        var one = await factory.LoginAsync(DemoCity.CitizenEmail);
        var bytes = DemoImages.Bytes("pothole_dupe");
        var a = await one.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("pothole_dupe.jpg", bytes, Spot(13).Lat, Spot(13).Lon));
        var created = await CreateNewAsync(one, a);

        var ex = await one.ExpectErrorAsync(() => one.PostFormAsync<AnalysisResponse>("/api/observations/analyze",
            ApiClient.PhotoForm("pothole_dupe.jpg", bytes, Spot(13).Lat, Spot(13).Lon)), HttpStatusCode.Conflict);
        Assert.Contains("already submitted this exact photo", ex.Message);

        var two = await factory.LoginAsync(DemoCity.CitizenTwoEmail);
        var a2 = await two.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("pothole_dupe.jpg", bytes, Spot(13).Lat, Spot(13).Lon));
        Assert.Contains(a2.Warnings, w => w.Contains("matches one submitted earlier"));
        Assert.Equal(100, Assert.Single(a2.Matches).SameIncidentPercent > 95 ? 100 : 0); // identical hash → max visual similarity
        var merged = await AttachAsync(two, a2, created.IncidentId);
        var d = await two.GetAsync<IncidentDetailDto>($"/api/incidents/{created.IncidentId}");
        Assert.Contains(d.Observations, o => o.SuspectedDuplicateImage);
        Assert.Equal(2, merged.ObservationCountAfter);
    }

    [Fact]
    public async Task Low_confidence_ai_asks_the_citizen_for_a_category_and_human_choice_is_recorded()
    {
        var c = await factory.LoginAsync(DemoCity.CitizenEmail);
        var a = await AnalyzeAsync(c, "IMG_20261011_0001.jpg", Spot(14));

        Assert.True(a.IsLowConfidence);
        Assert.InRange(a.ConfidencePercent, 40, 59);
        Assert.Equal(3, a.Alternatives.Count);                       // "Road Damage 51% / Pothole 32% / Other 17%" style
        Assert.Contains(a.Warnings, w => w.Contains("not fully sure"));

        var ex = await c.ExpectErrorAsync(() => CreateNewAsync(c, a), HttpStatusCode.BadRequest);
        Assert.Equal("category_required", ex.Code);

        var r = await CreateNewAsync(c, a, category: "Pothole");
        var d = await c.GetAsync<IncidentDetailDto>($"/api/incidents/{r.IncidentId}");
        Assert.Equal("Pothole", d.Summary.Category);
        if (a.DetectedCategory != "Pothole") Assert.Contains(d.Timeline, t => t.EventType == "CitizenCategoryOverride");

        // a submitted draft cannot be submitted twice
        await c.ExpectErrorAsync(() => CreateNewAsync(c, a, "Pothole"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Authority_override_requires_a_reason_changes_category_and_severity_and_is_audited()
    {
        var c = await factory.LoginAsync(DemoCity.CitizenEmail);
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var inc = await CreateNewAsync(c, await AnalyzeAsync(c, "pothole_override.jpg", Spot(15)));

        await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/override", new { category = "RoadDamage" }), HttpStatusCode.BadRequest);
        await authority.ExpectErrorAsync(() => authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/override", new { severity = 500, reason = "x" }), HttpStatusCode.BadRequest);

        var s = await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/override", new { category = "RoadDamage", severity = 95, reason = "Photo shows wider carriageway damage" });
        Assert.Equal("RoadDamage", s.Category);
        Assert.True(s.PriorityScore > inc.PriorityAfter);

        var d = await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{inc.IncidentId}");
        Assert.Equal(95, d.Severity);
        Assert.Contains(d.Timeline, t => t.EventType == "AuthorityOverrideCategory" && t.Message.Contains("wider carriageway"));
        Assert.Contains(d.Timeline, t => t.EventType == "AuthorityOverrideSeverity");
    }

    [Fact]
    public async Task Worker_cannot_reuse_the_before_photo_as_after_evidence()
    {
        var citizen = await factory.LoginAsync(DemoCity.CitizenEmail);
        var authority = await factory.LoginAsync(DemoCity.AuthorityEmail);
        var worker = await factory.LoginAsync(DemoCity.WorkerEmail);
        var bytes = DemoImages.Bytes("garbage_same");
        var a = await citizen.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("garbage_same.jpg", bytes, Spot(16).Lat, Spot(16).Lon));
        var inc = await CreateNewAsync(citizen, a);
        var w = (await authority.GetAsync<List<WorkerDto>>("/api/users/workers")).First();
        await authority.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/assign", new { workerId = w.Id });
        await worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "Accepted" });
        await worker.PostAsync<IncidentSummaryDto>($"/api/incidents/{inc.IncidentId}/status", new { status = "InProgress" });

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } }, "afterImage", "after.jpg");
        var ex = await worker.ExpectErrorAsync(() => worker.PostFormAsync<ResolutionDto>($"/api/incidents/{inc.IncidentId}/resolution", form), HttpStatusCode.BadRequest);
        Assert.Equal("duplicate_image", ex.Code);
        Assert.Equal("InProgress", (await authority.GetAsync<IncidentDetailDto>($"/api/incidents/{inc.IncidentId}")).Summary.Status);
    }

    // ===================================================================== notifications + envelope + auth
    [Fact]
    public async Task Assignment_notifies_the_worker_and_contributors_and_notifications_can_be_marked_read()
    {
        var (citizen, _, worker, _, _) = await InProgressIncidentAsync(17, "garbage_notify.jpg");
        var wn = await worker.GetAsync<List<NotificationDto>>("/api/users/me/notifications");
        Assert.Contains(wn, n => n.EventType == "incident_assigned" && !n.IsRead);
        await worker.PostAsync<object>("/api/users/me/notifications/read");
        Assert.All(await worker.GetAsync<List<NotificationDto>>("/api/users/me/notifications"), n => Assert.True(n.IsRead));
        Assert.Contains(await citizen.GetAsync<List<NotificationDto>>("/api/users/me/notifications"), n => n.EventType == "observation_accepted");
    }

    [Fact]
    public async Task Auth_register_login_failures_and_envelope_shape()
    {
        var c = factory.Client();
        var reg = await c.PostAsync<AuthResponse>("/api/auth/register", new { name = "New Person", email = "new.person@example.com", password = "longenough1" });
        Assert.Equal("Citizen", reg.User.Role);
        Assert.False(string.IsNullOrEmpty(reg.Token));
        Assert.DoesNotContain("passwordHash", c.LastBody, StringComparison.OrdinalIgnoreCase);

        await c.ExpectErrorAsync(() => c.PostAsync<AuthResponse>("/api/auth/register", new { name = "Dup", email = "new.person@example.com", password = "longenough1" }), HttpStatusCode.Conflict);
        await c.ExpectErrorAsync(() => c.PostAsync<AuthResponse>("/api/auth/register", new { name = "X", email = "not-an-email", password = "longenough1" }), HttpStatusCode.BadRequest);
        await c.ExpectErrorAsync(() => c.PostAsync<AuthResponse>("/api/auth/register", new { name = "X", email = "x@example.com", password = "short" }), HttpStatusCode.BadRequest);
        var bad = await c.ExpectErrorAsync(() => c.PostAsync<AuthResponse>("/api/auth/login", new { email = DemoCity.CitizenEmail, password = "wrong-password" }), HttpStatusCode.Unauthorized);
        Assert.Equal("Incorrect email or password.", bad.Detail);

        // self-registration can never create an elevated role
        var sneaky = await c.PostAsync<AuthResponse>("/api/auth/register", new { name = "Sneaky", email = "sneaky@example.com", password = "longenough1", role = "Authority" });
        Assert.Equal("Citizen", sneaky.User.Role);

        var res = await c.Http.GetAsync("/api/demo/info");
        Assert.True(res.Headers.Contains("X-Correlation-Id"));
        Assert.Contains("\"success\":true", await res.Content.ReadAsStringAsync());
    }

}

public class RateLimitTests
{
    [Fact]
    public async Task Configurable_observation_rate_limit_stops_spam_with_a_human_readable_429()
    {
        await using var factory = new ApiFactory { MaxObservationsPerHour = 2 };
        var c = await factory.LoginAsync(DemoCity.CitizenEmail);

        for (var i = 0; i < 2; i++)
        {
            var at = (12.9400 + i * 0.003, 77.6300);
            var a = await c.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm($"pothole_spam{i}.jpg", DemoImages.Bytes($"pothole{i}"), at.Item1, at.Item2));
            await c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = a.DraftId, action = "CreateNew" });
        }
        var third = await c.PostFormAsync<AnalysisResponse>("/api/observations/analyze", ApiClient.PhotoForm("pothole_spam3.jpg", DemoImages.Bytes("pothole3"), 12.9460, 77.6300));
        var ex = await c.ExpectErrorAsync(() => c.PostAsync<SubmitObservationResult>("/api/observations", new { draftId = third.DraftId, action = "CreateNew" }), HttpStatusCode.TooManyRequests);
        Assert.Equal("rate_limited", ex.Code);
        Assert.Contains("up to 2 observations per hour", ex.Message);
    }
}
