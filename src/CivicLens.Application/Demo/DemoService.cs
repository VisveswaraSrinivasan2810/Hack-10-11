using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Application.Dtos;
using CivicLens.Application.Services;
using CivicLens.Domain;
using CivicLens.Domain.Scoring;
using Microsoft.EntityFrameworkCore;

namespace CivicLens.Application.Demo;

public sealed record ActingUser(Guid? Id, UserRole? Role) : ICurrentUser;

/// <summary>
/// Developer/admin "Demo Scenario" runner. Every scenario drives the REAL services (analyze → match → submit → assign → resolve → verify)
/// with synthetic, deterministic photos, so what the jury sees is the actual pipeline — not canned screens.
/// </summary>
public sealed class DemoService(
    IAppDbContext db, ObservationService observations, IncidentService incidents, ResolutionService resolution,
    DemoSeeder seeder, IncidentLifecycle lifecycle)
{
    private static readonly DemoScenarioDto[] Scenarios =
    [
        new(1, "New unique pothole", "A citizen reports a pothole on a quiet road: new incident, low contextual risk."),
        new(2, "Duplicate pothole observation", "A second citizen photographs the same pothole 11 m away: system proposes the existing incident and merges it."),
        new(3, "Critical pothole near school", "Two citizens report a pothole ~60 m from a school: contextual risk pushes CPS to CRITICAL."),
        new(4, "Garbage accumulation", "Garbage accumulation reported at Old Town Lane."),
        new(5, "Water leakage", "Water leakage reported in Old Town."),
        new(6, "Recurring road damage", "Road damage at Location X, which already had pothole (Jan), road damage (Apr), pothole (Jul): RECURRING INFRASTRUCTURE ISSUE."),
        new(7, "Successful resolution", "Full lifecycle: report → assign → work → after-photo → AI check → citizen confirmation → VERIFIED RESOLVED."),
        new(8, "Failed resolution verification", "Ghost closure: worker marks complete but the after-photo still shows the issue → RESOLUTION DISPUTED, REQUIRES REVIEW."),
        new(9, "Cross-issue correlation", "Water leakage followed 10 days later by road damage 18 m away: a POSSIBLE relationship is surfaced."),
        new(10, "Civic hotspot", "Four incidents of different types appear within ~150 m: an emerging hotspot forms."),
    ];

    public IReadOnlyList<DemoScenarioDto> List() => Scenarios;

    public Task ResetAsync(CancellationToken ct = default) => seeder.ResetAsync(ct);

    public async Task<DemoScenarioResult> RunAsync(int id, CancellationToken ct = default)
    {
        var scenario = Scenarios.FirstOrDefault(s => s.Id == id) ?? throw new NotFoundException("Unknown demo scenario.");
        var authority = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Role == UserRole.Authority, ct)
                        ?? throw new ConflictException("Demo data is not seeded. Reset the demo first.");
        var worker = await db.Users.AsNoTracking().FirstAsync(u => u.Email == DemoCity.WorkerEmail, ct);

        return id switch
        {
            1 => Result(scenario, await ReportAsync(ct, "pothole", DemoCity.QuietRoadLat, DemoCity.QuietRoadLon, "Pothole on a quiet street"), "New incident created on a quiet road."),
            2 => await DuplicateAsync(scenario, ct),
            3 => await SchoolAsync(scenario, ct),
            4 => Result(scenario, await ReportAsync(ct, "garbage", DemoCity.GarbageLat, DemoCity.GarbageLon, "Overflowing garbage"), "Garbage accumulation recorded."),
            5 => Result(scenario, await ReportAsync(ct, "water_leak", DemoCity.WaterLat, DemoCity.WaterLon, "Water leaking onto the road"), "Water leakage recorded."),
            6 => Result(scenario, await ReportAsync(ct, "road_damage", DemoCity.RecurringXLat, DemoCity.RecurringXLon, "Road surface broken again", forceNew: true),
                "Recurrence detected from Infrastructure Memory."),
            7 => await ResolutionAsync(scenario, authority, worker, success: true, ct),
            8 => await ResolutionAsync(scenario, authority, worker, success: false, ct),
            9 => await CorrelationAsync(scenario, ct),
            10 => await HotspotAsync(scenario, ct),
            _ => throw new NotFoundException("Unknown demo scenario.")
        };
    }

    // ---------------------------------------------------------------- scenarios
    private async Task<DemoScenarioResult> DuplicateAsync(DemoScenarioDto s, CancellationToken ct)
    {
        var first = await ReportAsync(ct, "pothole", DemoCity.DuplicateLat, DemoCity.DuplicateLon, "Pothole on a quiet street");
        var second = await ReportAsync(ct, "pothole", DemoCity.DuplicateLat + 0.0001, DemoCity.DuplicateLon, "Same pothole, another citizen");
        var hl = new List<string>
        {
            $"First observation → {first.Code} (CPS {first.PriorityAfter:0})",
            $"Second citizen was offered {second.Code} as a likely match and confirmed it",
            $"Citizen observations {second.ObservationCountBefore} → {second.ObservationCountAfter}",
            $"Civic Priority {second.PriorityBefore:0} → {second.PriorityAfter:0} ({second.PriorityBand})"
        };
        return new DemoScenarioResult(s.Id, s.Name, second.IncidentId, second.Code, second.Message, hl);
    }

    private async Task<DemoScenarioResult> SchoolAsync(DemoScenarioDto s, CancellationToken ct)
    {
        var first = await ReportAsync(ct, "pothole_deep", DemoCity.LakeviewSchoolLat, DemoCity.LakeviewSchoolLon, "Deep pothole outside the school");
        var second = await ReportAsync(ct, "pothole", DemoCity.LakeviewSchoolLat, DemoCity.LakeviewSchoolLon + 0.0001, "Pothole near school gate");
        var detail = await incidents.GetDetailAsync(second.IncidentId, new ActingUser(null, UserRole.Authority), ct);
        var hl = new List<string> { $"CPS {first.PriorityAfter:0} → {second.PriorityAfter:0} ({second.PriorityBand})" };
        hl.AddRange(detail.Priority.Reasons);
        return new DemoScenarioResult(s.Id, s.Name, second.IncidentId, second.Code, second.Message, hl);
    }

    private async Task<DemoScenarioResult> ResolutionAsync(DemoScenarioDto s, Domain.Entities.User authority, Domain.Entities.User worker, bool success, CancellationToken ct)
    {
        var author = new ActingUser(authority.Id, UserRole.Authority);
        var wk = new ActingUser(worker.Id, UserRole.FieldWorker);
        var citizen = await PickCitizenAsync(ct);
        var cit = new ActingUser(citizen.Id, UserRole.Citizen);

        var r = await ReportAsync(ct, "garbage", DemoCity.GarbageLat + 0.0012, DemoCity.GarbageLon - 0.0012, "Garbage pile on the footpath", forceNew: true, citizen: citizen);
        await incidents.AssignAsync(r.IncidentId, new AssignRequest(worker.Id, "Demo scenario"), author, ct);
        await incidents.ChangeStatusAsync(r.IncidentId, new StatusChangeRequest("Accepted", null), wk, ct);
        await incidents.ChangeStatusAsync(r.IncidentId, new StatusChangeRequest("InProgress", null), wk, ct);

        var after = DemoImages.Upload(success ? "garbage_fixed_after" : "garbage_still_present_after");
        var res = await resolution.SubmitAsync(r.IncidentId, wk, after, success ? "Garbage cleared and area swept." : "Cleared and reported complete.", null, ct);
        if (success) res = await resolution.VoteAsync(r.IncidentId, cit, new VoteRequest("Fixed", null, null), ct);

        var hl = new List<string>
        {
            $"Before: {res.BeforeSummary}", $"After: {res.AfterSummary}", $"AI verification: {res.AiConfidencePercent}%",
            $"Resolution confidence: {res.ResolutionConfidencePercent}%", res.OutcomeLabel
        };
        if (res.NeutralNote is not null) hl.Add(res.NeutralNote);
        return new DemoScenarioResult(s.Id, s.Name, r.IncidentId, r.Code, res.OutcomeLabel, hl);
    }

    private async Task<DemoScenarioResult> CorrelationAsync(DemoScenarioDto s, CancellationToken ct)
    {
        var leak = await ReportAsync(ct, "water_leak", DemoCity.LeakCorrLat, DemoCity.LeakCorrLon, "Water leaking from a pipe", forceNew: true);
        // Simulate that the leak was reported 10 days ago (the demo cannot wait 10 real days).
        var leakInc = await db.Incidents.FirstAsync(i => i.Id == leak.IncidentId, ct);
        leakInc.FirstObservedAt = lifecycle.Now.AddDays(-10);
        leakInc.LastObservedAt = leakInc.FirstObservedAt;
        lifecycle.AddEvent(leakInc.Id, "DemoNote", "Demo scenario: this report was backdated by 10 days to simulate elapsed time.", null);
        await db.SaveChangesAsync(ct);

        var road = await ReportAsync(ct, "road_damage", DemoCity.LeakCorrLat + 0.00016, DemoCity.LeakCorrLon, "Road surface collapsing", forceNew: true);
        var corr = (await incidents.CorrelationsForAsync(road.IncidentId, ct)).FirstOrDefault();
        var hl = corr is null
            ? new List<string> { "No correlation was produced (unexpected)." }
            : [$"{corr.SourceCategoryLabel} {corr.SourceCode} → {corr.TargetCategoryLabel} {corr.TargetCode}", $"Distance {corr.DistanceMeters} m",
               $"Confidence: {corr.ConfidenceLabel}", corr.Explanation];
        return new DemoScenarioResult(s.Id, s.Name, road.IncidentId, road.Code, "Possible infrastructure relationship detected.", hl);
    }

    private async Task<DemoScenarioResult> HotspotAsync(DemoScenarioDto s, CancellationToken ct)
    {
        var codes = new List<string>();
        (string Hint, string Desc)[] kinds = [("pothole", "Pothole"), ("water_leak", "Water leak"), ("road_damage", "Cracked road"), ("pothole_deep", "Deep pothole")];
        SubmitObservationResult? last = null;
        for (var k = 0; k < kinds.Length; k++)
        {
            last = await ReportAsync(ct, kinds[k].Hint, 12.9660 + 0.0007 * k, 77.6060 + 0.0004 * k, kinds[k].Desc);
            codes.Add(last.Code);
        }
        var hs = await db.Incidents.CountAsync(i => i.Latitude >= 12.9655 && i.Latitude <= 12.9690 && i.Longitude >= 77.6050 && i.Longitude <= 77.6085
                                                 && i.Status != IncidentStatus.Rejected && i.Status != IncidentStatus.VerifiedResolved, ct);
        return new DemoScenarioResult(s.Id, s.Name, last!.IncidentId, last.Code, "Incident cluster created.",
            [$"Incidents: {string.Join(", ", codes.Distinct())}", $"{hs} incidents now within ~150 m of each other — see Hotspots on the dashboard."]);
    }

    // ---------------------------------------------------------------- helpers
    private DemoScenarioResult Result(DemoScenarioDto s, SubmitObservationResult r, string summary)
    {
        var hl = new List<string>
        {
            $"{r.Code}: CPS {r.PriorityAfter:0} ({r.PriorityBand})", r.IsNewIncident ? "New incident created" : $"Merged — {r.ObservationCountAfter} observations"
        };
        hl.AddRange(r.Reasons.Take(4));
        if (r.Recurrence is { IsRecurring: true } rec) { hl.Insert(0, rec.Banner!); hl.Insert(1, rec.Summary); }
        return new DemoScenarioResult(s.Id, s.Name, r.IncidentId, r.Code, summary, hl);
    }

    /// <summary>Runs the real analyze → decide → submit flow. A likely match is accepted (as a citizen would) unless forceNew.</summary>
    private async Task<SubmitObservationResult> ReportAsync(
        CancellationToken ct, string hint, double lat, double lon, string description, bool forceNew = false, Domain.Entities.User? citizen = null)
    {
        citizen ??= await PickCitizenAsync(ct);
        var analysis = await observations.AnalyzeAsync(citizen.Id, new AnalyzeRequest(DemoImages.Upload(hint), lat, lon, description, null, null, null), ct);
        var match = analysis.Matches.FirstOrDefault();
        var attach = !forceNew && match is not null;
        return await observations.SubmitAsync(citizen.Id,
            new SubmitObservationRequest(analysis.DraftId, null, description, attach ? "AttachToExisting" : "CreateNew", attach ? match!.IncidentId : null), ct);
    }

    private async Task<Domain.Entities.User> PickCitizenAsync(CancellationToken ct)
    {
        var since = lifecycle.Now.AddHours(-1);
        var citizens = await db.Users.AsNoTracking().Where(u => u.Role == UserRole.Citizen).ToListAsync(ct);
        if (citizens.Count == 0) throw new ConflictException("Demo data is not seeded. Reset the demo first.");
        var recent = await db.Observations.AsNoTracking().Where(o => o.CreatedAt >= since).GroupBy(o => o.UserId)
            .Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        // Rotate across accounts so repeated scenario runs never trip the per-user rate limit.
        return citizens.OrderBy(c => recent.FirstOrDefault(r => r.Key == c.Id)?.N ?? 0).ThenBy(c => c.Email).First();
    }
}
