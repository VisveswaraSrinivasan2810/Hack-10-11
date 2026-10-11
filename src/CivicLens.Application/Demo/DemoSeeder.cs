using CivicLens.Application.Abstractions;
using CivicLens.Application.Services;
using CivicLens.Domain;
using CivicLens.Domain.Entities;
using CivicLens.Domain.Scoring;
using Microsoft.EntityFrameworkCore;

namespace CivicLens.Application.Demo;

/// <summary>
/// Seeds the fictional demo city: users, zones, POIs, 26 incidents (15 active, 10 resolved, 1 rejected), recurrence history,
/// disputed resolutions, a hotspot and a water-leak → road-damage correlation. Scores are produced by the real scoring services.
/// </summary>
public sealed class DemoSeeder(
    IAppDbContext db, IPasswordHasher hasher, IPriorityScoringService priority, CorrelationService correlation,
    TrustService trust, TimeProvider clock)
{
    private sealed record Spec(
        IssueCategory Category, double Lat, double Lon, string Label, int DaysAgo, IncidentStatus Status, int Obs,
        int Severity = 0, int Confirmations = 0, bool Worker = false, int ResolveHours = 0, string[]? Path = null);

    private DateTime _now;
    private List<User> _citizens = [];
    private User _authority = null!, _worker = null!;
    private List<Zone> _zones = [];

    public async Task<bool> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct)) return false;
        await SeedAsync(ct);
        return true;
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        async Task Clear<T>(DbSet<T> set) where T : class { set.RemoveRange(await set.ToListAsync(ct)); await db.SaveChangesAsync(ct); }
        await Clear(db.IncidentComments); await Clear(db.IncidentUpvotes);
        await Clear(db.Notifications); await Clear(db.CitizenVerifications); await Clear(db.ResolutionEvidence);
        await Clear(db.Correlations); await Clear(db.History); await Clear(db.PriorityBreakdowns); await Clear(db.Assignments);
        await Clear(db.IncidentConfirmations); await Clear(db.IncidentObservations); await Clear(db.Incidents);
        await Clear(db.Observations); await Clear(db.ObservationDrafts); await Clear(db.PointsOfInterest);
        await Clear(db.Zones); await Clear(db.Users);
        await SeedAsync(ct);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        _now = clock.GetUtcNow().UtcDateTime;
        await SeedUsersAsync(ct);
        await SeedZonesAndPoisAsync(ct);

        var specs = BuildSpecs().OrderByDescending(s => s.DaysAgo).ToList(); // oldest first → recurrence history exists when newer ones are scored
        var incidents = new List<CivicIncident>();
        var n = 0;
        foreach (var spec in specs) incidents.Add(await CreateIncidentAsync(spec, 1001 + n++, ct));

        foreach (var inc in incidents)
        {
            await priority.RecalculateAsync(inc, "seed", null, ct);
            // Replace the auto "now"-stamped score event with one at creation time so seeded timelines read chronologically.
            var auto = await db.History.Where(h => h.IncidentId == inc.Id && h.EventType == "PriorityChanged").ToListAsync(ct);
            db.History.RemoveRange(auto);
            db.History.Add(new IncidentHistory
            {
                IncidentId = inc.Id, EventType = "PriorityChanged", CreatedAt = inc.FirstObservedAt.AddMinutes(2),
                Message = $"CPS set to {inc.PriorityScore:0} ({CpsCalculator.BandLabel(inc.PriorityBand)})"
            });
            await db.SaveChangesAsync(ct);
        }
        foreach (var inc in incidents.Where(i => i.Category is IssueCategory.RoadDamage or IssueCategory.Pothole))
            await correlation.DetectForTargetAsync(inc, ct);

        await trust.RefreshAsync(_citizens.Select(c => c.Id), ct);
    }

    // ------------------------------------------------------------------ users / geography
    private async Task SeedUsersAsync(CancellationToken ct)
    {
        User Make(string name, string email, UserRole role) =>
            new() { Name = name, Email = email, Role = role, PasswordHash = hasher.Hash(DemoCity.DemoPassword), CreatedAt = _now.AddDays(-400) };

        _authority = Make("Meera Rao (Municipal Authority)", DemoCity.AuthorityEmail, UserRole.Authority);
        _worker = Make("Ravi Kumar (Field Worker)", DemoCity.WorkerEmail, UserRole.FieldWorker);
        var extraWorker = Make("Sana Iqbal (Field Worker)", "worker2@civiclens.demo", UserRole.FieldWorker);
        _citizens =
        [
            Make("Asha Citizen", DemoCity.CitizenEmail, UserRole.Citizen),
            Make("Dev Citizen", DemoCity.CitizenTwoEmail, UserRole.Citizen),
            .. new[] { "Aarav", "Bhavna", "Chirag", "Divya", "Esha", "Farhan", "Gita", "Harsh" }
                .Select(n => Make($"{n} Neighbour", $"{n.ToLowerInvariant()}@civiclens.demo", UserRole.Citizen))
        ];
        db.Users.AddRange(_authority, _worker, extraWorker);
        db.Users.AddRange(_citizens);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedZonesAndPoisAsync(CancellationToken ct)
    {
        _zones = DemoCity.Zones.Select(z => new Zone
        {
            Name = z.Name, CenterLatitude = (z.MinLat + z.MaxLat) / 2, CenterLongitude = (z.MinLon + z.MaxLon) / 2,
            Boundary = GeoMath.ToPolygon([(z.MinLat, z.MinLon), (z.MinLat, z.MaxLon), (z.MaxLat, z.MaxLon), (z.MaxLat, z.MinLon)])
        }).ToList();
        db.Zones.AddRange(_zones);
        db.PointsOfInterest.AddRange(DemoCity.Pois.Select(p => new PointOfInterest
        { Name = p.Name, Type = p.Type, Latitude = p.Lat, Longitude = p.Lon, Location = GeoMath.ToPoint(p.Lat, p.Lon), RiskWeight = p.Weight }));
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ incident specs
    private static readonly string[] ResolvedPath = ["Assigned", "Accepted", "InProgress", "WorkCompleted", "AwaitingVerification", "VerifiedResolved"];
    private static readonly string[] DisputedPath = ["Assigned", "Accepted", "InProgress", "WorkCompleted", "AwaitingVerification", "VerificationFailed", "RequiresReview"];

    private static List<Spec> BuildSpecs()
    {
        const IncidentStatus R = IncidentStatus.VerifiedResolved;
        var list = new List<Spec>
        {
            // --- Recurring Location X (Koti): pothole / road damage / pothole, all resolved
            new(IssueCategory.Pothole, 17.38350, 78.49510, "Koti Demo Road, 5th Cross", 270, R, 2, ResolveHours: 52, Path: ResolvedPath),
            new(IssueCategory.RoadDamage, 17.38352, 78.49513, "Koti Demo Road, 5th Cross", 180, R, 3, ResolveHours: 61, Path: ResolvedPath),
            new(IssueCategory.Pothole, 17.38348, 78.49508, "Koti Demo Road, 5th Cross", 90, R, 2, ResolveHours: 47, Path: ResolvedPath),
            // --- Recurring Location Y (Abids Demo Avenue): three resolved + one active
            new(IssueCategory.RoadDamage, 17.38940, 78.48460, "Abids Demo Avenue", 300, R, 2, ResolveHours: 70, Path: ResolvedPath),
            new(IssueCategory.Pothole, 17.38942, 78.48462, "Abids Demo Avenue", 200, R, 2, ResolveHours: 55, Path: ResolvedPath),
            new(IssueCategory.RoadDamage, 17.38938, 78.48458, "Abids Demo Avenue", 110, R, 3, ResolveHours: 66, Path: ResolvedPath),
            new(IssueCategory.RoadDamage, 17.38941, 78.48461, "Abids Demo Avenue", 12, IncidentStatus.Validated, 2, Path: ["Validated"]),
            // --- Recurring Location Z (Lakdikapul Demo Main Road): three resolved
            new(IssueCategory.Pothole, 17.38140, 78.47610, "Lakdikapul Demo Main Road", 240, R, 2, ResolveHours: 44, Path: ResolvedPath),
            new(IssueCategory.Pothole, 17.38142, 78.47612, "Lakdikapul Demo Main Road", 150, R, 2, ResolveHours: 58, Path: ResolvedPath),
            new(IssueCategory.RoadDamage, 17.38138, 78.47608, "Lakdikapul Demo Main Road", 75, R, 2, ResolveHours: 49, Path: ResolvedPath),
            // --- Other resolved
            new(IssueCategory.GarbageAccumulation, 17.38990, 78.50010, "Koti Demo Lane 4", 20, R, 3, ResolveHours: 30, Path: ResolvedPath),

            // --- Emerging hotspot at Abids Demo Junction (6 active incidents within ~100 m)
            new(IssueCategory.Pothole, 17.38650, 78.49070, "Abids Demo Junction, north arm", 2, IncidentStatus.Reported, 2),
            new(IssueCategory.Pothole, 17.38635, 78.49050, "Abids Demo Junction, west arm", 4, IncidentStatus.Validated, 3, Path: ["Validated"]),
            new(IssueCategory.RoadDamage, 17.38660, 78.49080, "Abids Demo Junction, signal pole", 6, IncidentStatus.Assigned, 2, Worker: true, Path: ["Assigned"]),
            new(IssueCategory.RoadDamage, 17.38625, 78.49040, "Abids Demo Junction, south arm", 9, IncidentStatus.Reported, 1),
            new(IssueCategory.WaterLeakage, 17.38645, 78.49090, "Abids Demo Junction, east arm", 11, IncidentStatus.InProgress, 3, Worker: true, Path: ["Assigned", "Accepted", "InProgress"]),
            new(IssueCategory.WaterLeakage, 17.38630, 78.49070, "Abids Demo Junction, underpass", 20, IncidentStatus.Reported, 1),

            // --- Cross-issue correlation pair (Zone A)
            new(IssueCategory.WaterLeakage, 17.38890, 78.47910, "Lakdikapul Demo 2nd Main", 35, IncidentStatus.Assigned, 2, Worker: true, Path: ["Assigned"]),
            new(IssueCategory.RoadDamage, 17.38905, 78.47910, "Lakdikapul Demo 2nd Main", 22, IncidentStatus.Reported, 2),

            // --- Critical incidents near sensitive places
            new(IssueCategory.SewageOverflow, 17.38795, 78.48880, "Abids Demo Hospital approach", 3, IncidentStatus.Assigned, 5, Severity: 90, Worker: true, Path: ["Assigned"]),
            new(IssueCategory.Pothole, 17.38415, 78.48610, "Abids Demo School Road (south)", 5, IncidentStatus.Reported, 4, Severity: 84),
            new(IssueCategory.RoadDamage, 17.38265, 78.49615, "Koti Demo Market Road", 5, IncidentStatus.Validated, 4, Severity: 88, Path: ["Validated"]),

            // --- Disputed resolutions ("ghost closures")
            new(IssueCategory.GarbageAccumulation, 17.39060, 78.49710, "Koti Demo Lane 9", 15, IncidentStatus.RequiresReview, 3, Confirmations: 2, ResolveHours: 40, Path: DisputedPath),
            new(IssueCategory.BrokenStreetlight, 17.38020, 78.47760, "Lakdikapul Demo 5th Cross", 18, IncidentStatus.RequiresReview, 2, ResolveHours: 36, Path: DisputedPath),

            // --- Work in progress assigned to the demo worker
            new(IssueCategory.GarbageAccumulation, 17.38920, 78.47360, "Lakdikapul Demo Park Gate", 7, IncidentStatus.InProgress, 3, Worker: true, Path: ["Assigned", "Accepted", "InProgress"]),

            // --- Invalid report (demonstrates trust penalty + audit)
            new(IssueCategory.Other, 17.37940, 78.50110, "Koti demo edge", 10, IncidentStatus.Rejected, 1, Path: ["Rejected"]),
        };
        return list;
    }

    private async Task<CivicIncident> CreateIncidentAsync(Spec s, int number, CancellationToken ct)
    {
        var first = _now.AddDays(-s.DaysAgo);
        var severity = s.Severity > 0 ? s.Severity : CategoryRules.BaseSeverity(s.Category);
        var point = GeoMath.ToPoint(s.Lat, s.Lon);
        var inc = new CivicIncident
        {
            PublicIncidentCode = $"CL-{number}", Category = s.Category, Latitude = s.Lat, Longitude = s.Lon, Location = point,
            LocationLabel = s.Label, PrimaryImageUrl = DemoImages.SeedUrl(s.Category), SeverityScore = severity, Status = s.Status,
            FirstObservedAt = first, LastObservedAt = s.Obs > 1 ? _now.AddDays(-Math.Max(0, s.DaysAgo - 2)) : first,
            ObservationCount = s.Obs, ConfirmationCount = s.Confirmations, ZoneId = _zones.FirstOrDefault(z => z.Boundary.Contains(point))?.Id,
            CreatedAt = first, UpdatedAt = first, ResolutionStatus = IncidentStateMachine.ResolutionFor(s.Status)
        };
        if (s.Worker) inc.AssignedWorkerId = _worker.Id;
        var resolved = s.Status == IncidentStatus.VerifiedResolved;
        if (resolved) inc.ResolvedAt = first.AddHours(s.ResolveHours);
        if (s.Status == IncidentStatus.RequiresReview) { inc.AssignedWorkerId = _worker.Id; inc.UpdatedAt = first.AddHours(s.ResolveHours); }
        db.Incidents.Add(inc);
        await db.SaveChangesAsync(ct);

        // Observations from distinct citizens
        var contributors = new List<User>();
        for (var j = 0; j < s.Obs; j++)
        {
            var user = _citizens[(number + j) % _citizens.Count];
            contributors.Add(user);
            var at = j == 0 ? first : first.AddHours(3 + j * 5);
            var rejected = s.Status == IncidentStatus.Rejected;
            var obs = new Observation
            {
                UserId = user.Id, ImageUrl = DemoImages.SeedUrl(s.Category), ImageHash = Guid.NewGuid().ToString("N"),
                ImageSignature = "", Latitude = s.Lat, Longitude = s.Lon, Location = point, DetectedCategory = s.Category,
                CitizenSelectedCategory = s.Category, AiConfidence = 0.9, SeverityScore = severity, CreatedAt = at,
                ValidationStatus = rejected ? ValidationStatus.Invalid
                    : s.Obs >= 2 || s.Status != IncidentStatus.Reported ? ValidationStatus.Verified : ValidationStatus.Pending
            };
            db.Observations.Add(obs);
            await db.SaveChangesAsync(ct);
            db.IncidentObservations.Add(new IncidentObservation
            {
                IncidentId = inc.Id, ObservationId = obs.Id, SameIncidentScore = j == 0 ? 1 : 0.9 + j % 3 * 0.02,
                MergeReason = j == 0 ? "NewIncident" : "CitizenConfirmedHighConfidence", CreatedAt = at
            });
            Hist(inc, j == 0 ? "ObservationSubmitted" : "ObservationMerged",
                j == 0 ? "Observation submitted" : $"Observation #{j + 1} merged (same-incident confidence {0.9 + j % 3 * 0.02:P0})", at, user.Id);
            if (j == 0)
            {
                Hist(inc, "AiClassified", $"AI classified {CategoryRules.Label(s.Category).ToLowerInvariant()} (90%)", at.AddSeconds(20));
                Hist(inc, "IncidentCreated", $"Incident {inc.PublicIncidentCode} created", at.AddMinutes(1), null, null, IncidentStatus.Reported);
            }
        }

        var confirmers = new List<User>();
        for (var j = 0; j < s.Confirmations; j++)
        {
            var user = _citizens[(number + s.Obs + j + 3) % _citizens.Count];
            if (contributors.Contains(user) || confirmers.Contains(user)) continue;
            confirmers.Add(user);
            db.IncidentConfirmations.Add(new IncidentConfirmation { IncidentId = inc.Id, UserId = user.Id, CreatedAt = first.AddHours(8 + j) });
            Hist(inc, "CitizenConfirmed", "A citizen confirmed the incident", first.AddHours(8 + j), user.Id);
        }
        inc.ConfirmationCount = confirmers.Count;

        // Replay the operational path
        var prev = IncidentStatus.Reported;
        var t = first.AddHours(6);
        foreach (var step in s.Path ?? [])
        {
            var to = Enum.Parse<IncidentStatus>(step);
            var msg = to switch
            {
                IncidentStatus.Assigned => $"Assigned to {_worker.Name}",
                IncidentStatus.WorkCompleted => "Work completed — after-photo uploaded",
                IncidentStatus.AwaitingVerification => "Verification requested",
                IncidentStatus.VerifiedResolved => "Verified resolved",
                IncidentStatus.VerificationFailed => "Verification failed — visual check did not confirm the fix",
                IncidentStatus.RequiresReview => "Resolution disputed — requires review",
                IncidentStatus.Rejected => "Rejected — not a valid civic issue",
                _ => $"Status changed to {to}"
            };
            Hist(inc, "StatusChanged", msg, t, to is IncidentStatus.Assigned or IncidentStatus.Validated or IncidentStatus.Rejected ? _authority.Id : null, prev, to);
            if (to == IncidentStatus.Assigned)
                db.Assignments.Add(new IncidentAssignment
                { IncidentId = inc.Id, WorkerId = _worker.Id, AssignedBy = _authority.Id, AssignedAt = t, IsActive = s.Worker || s.Status == IncidentStatus.RequiresReview });
            prev = to;
            t = t.AddHours(s.ResolveHours > 0 ? Math.Max(2, s.ResolveHours / 6.0) : 3);
        }

        if (s.Status is IncidentStatus.VerifiedResolved or IncidentStatus.RequiresReview)
        {
            var disputed = s.Status == IncidentStatus.RequiresReview;
            var ev = new ResolutionEvidence
            {
                IncidentId = inc.Id, WorkerId = _worker.Id, BeforeImageUrl = inc.PrimaryImageUrl,
                AfterImageUrl = disputed ? DemoImages.SeedUrl(s.Category) : DemoImages.FixedUrl, AfterImageHash = Guid.NewGuid().ToString("N"),
                WorkerNotes = disputed ? "Cleared and reported complete." : "Repair completed and area cleaned.",
                AiConfidence = disputed ? 0.24 : 0.95,
                VerificationResult = disputed ? VerificationResult.IssueStillPresent : VerificationResult.IssueNotDetected,
                AiSummary = disputed
                    ? $"{CategoryRules.Label(s.Category)} detected|{CategoryRules.Label(s.Category)} may still be visible|Visual verification indicates the issue may still be present."
                    : $"{CategoryRules.Label(s.Category)} detected|{CategoryRules.Label(s.Category)} not detected|Issue no longer detected in the after-photo.",
                CompletedAt = first.AddHours(s.ResolveHours - 2), SubmittedAt = first.AddHours(s.ResolveHours - 1)
            };
            db.ResolutionEvidence.Add(ev);
            await db.SaveChangesAsync(ct);

            var voters = contributors.Concat(confirmers).Distinct().ToList();
            for (var j = 0; j < voters.Count; j++)
            {
                var vote = !disputed ? (j < 3 ? CitizenVote.Fixed : CitizenVote.Unsure)
                    : voters.Count >= 5 && j == voters.Count - 1 ? CitizenVote.Fixed : CitizenVote.StillExists;
                db.CitizenVerifications.Add(new CitizenVerification
                { IncidentId = inc.Id, ResolutionEvidenceId = ev.Id, UserId = voters[j].Id, Vote = vote, CreatedAt = first.AddHours(s.ResolveHours + j) });
            }
            ev.ResolutionConfidence = disputed ? 0.24 : 0.93;
            Hist(inc, "ResolutionChecked", disputed
                ? "AI verification 24% — Visual verification indicates the issue may still be present."
                : "AI verification 95% — Issue no longer detected in the after-photo.", ev.SubmittedAt.AddMinutes(1),
                null);
        }

        await db.SaveChangesAsync(ct);
        return inc;
    }

    private void Hist(CivicIncident inc, string type, string message, DateTime at, Guid? actor = null,
        IncidentStatus? old = null, IncidentStatus? @new = null) =>
        db.History.Add(new IncidentHistory
        { IncidentId = inc.Id, EventType = type, Message = message, CreatedAt = at, ActorId = actor, OldStatus = old, NewStatus = @new });
}
