using System.Text.Json;
using CivicLens.Application.Abstractions;
using CivicLens.Application.Dtos;
using CivicLens.Domain;
using CivicLens.Domain.Configuration;
using CivicLens.Domain.Entities;
using CivicLens.Domain.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CivicLens.Application.Services;

// ============================ Context risk ============================

public sealed class ContextRiskService(IAppDbContext db) : IContextRiskService
{
    public async Task<ContextRiskResult> AssessAsync(IssueCategory category, double lat, double lon, CancellationToken ct = default)
    {
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(lat, lon, ContextRiskCalculator.MaxSearchRadius);
        var pois = await db.PointsOfInterest.AsNoTracking()
            .Where(p => p.Latitude >= minLat && p.Latitude <= maxLat && p.Longitude >= minLon && p.Longitude <= maxLon)
            .ToListAsync(ct);
        var hits = pois.Select(p => new PoiHit(p.Name, p.Type, p.RiskWeight, GeoMath.DistanceMeters(lat, lon, p.Latitude, p.Longitude)));
        return ContextRiskCalculator.Calculate(category, hits);
    }
}

// ============================ Matching (fingerprint) ============================

public sealed record IncidentMatch(CivicIncident Incident, SameIncidentScore Score, bool IsResolved);

public interface IIncidentMatchingService
{
    Task<IReadOnlyList<IncidentMatch>> FindMatchesAsync(
        double lat, double lon, IssueCategory category, string imageSignature, string imageHash, DateTime now, CancellationToken ct = default);
}

public sealed class IncidentMatchingService(
    IAppDbContext db, IImageSimilarityService similarity, IOptions<ScoringOptions> options) : IIncidentMatchingService
{
    private readonly ScoringOptions _o = options.Value;

    public async Task<IReadOnlyList<IncidentMatch>> FindMatchesAsync(
        double lat, double lon, IssueCategory category, string imageSignature, string imageHash, DateTime now, CancellationToken ct = default)
    {
        // Step 1: spatial pre-filter (bounding box on indexed lat/lon, then exact distance).
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(lat, lon, _o.MatchRadiusMeters);
        var resolvedCutoff = now.AddDays(-_o.ResolvedCandidateDays);
        var nearby = await db.Incidents.AsNoTracking()
            .Where(i => i.Latitude >= minLat && i.Latitude <= maxLat && i.Longitude >= minLon && i.Longitude <= maxLon)
            .Where(i => i.Status != IncidentStatus.Rejected)
            .Where(i => i.Status != IncidentStatus.VerifiedResolved || i.ResolvedAt >= resolvedCutoff)
            .ToListAsync(ct);

        // Step 2: same / related category only.
        var candidates = nearby
            .Select(i => (Incident: i, Distance: GeoMath.DistanceMeters(lat, lon, i.Latitude, i.Longitude)))
            .Where(x => x.Distance <= _o.MatchRadiusMeters && CategoryRules.Similarity(category, x.Incident.Category) > 0)
            .ToList();
        if (candidates.Count == 0) return [];

        var ids = candidates.Select(c => c.Incident.Id).ToList();
        var evidence = await (from io in db.IncidentObservations
                              join o in db.Observations on io.ObservationId equals o.Id
                              where ids.Contains(io.IncidentId)
                              select new { io.IncidentId, o.ImageSignature, o.ImageHash }).AsNoTracking().ToListAsync(ct);

        var matches = new List<IncidentMatch>();
        foreach (var (incident, distance) in candidates)
        {
            // Step 4: visual similarity = best match against any observation already in the incident.
            double visual = 0;
            foreach (var e in evidence.Where(e => e.IncidentId == incident.Id))
                visual = Math.Max(visual, await similarity.CompareAsync(imageSignature, imageHash, e.ImageSignature, e.ImageHash, ct));

            var days = (now - incident.LastObservedAt).TotalDays;
            var score = SameIncidentScorer.Score(distance, category, incident.Category, visual, days, _o);
            if (score.Verdict != MatchVerdict.LikelyNew)
                matches.Add(new IncidentMatch(incident, score, incident.Status == IncidentStatus.VerifiedResolved));
        }
        return matches.OrderByDescending(m => m.Score.Total).Take(3).ToList();
    }
}

// ============================ Recurrence + correlation ============================

public sealed class RecurrenceService(IAppDbContext db, IOptions<ScoringOptions> options)
{
    private readonly ScoringOptions _o = options.Value;

    public async Task<RecurrenceResult> EvaluateAsync(
        IssueCategory category, double lat, double lon, DateTime now, Guid? excludeIncidentId, CancellationToken ct = default)
    {
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(lat, lon, _o.RecurrenceRadiusMeters);
        var cutoff = now.AddDays(-_o.RecurrenceWindowDays);
        var history = await db.Incidents.AsNoTracking()
            .Where(i => i.Id != excludeIncidentId && i.Status == IncidentStatus.VerifiedResolved)
            .Where(i => i.Latitude >= minLat && i.Latitude <= maxLat && i.Longitude >= minLon && i.Longitude <= maxLon)
            .Where(i => i.FirstObservedAt >= cutoff)
            .ToListAsync(ct);

        var priors = history.Select(i => new PriorIncident(
            i.Id, i.PublicIncidentCode, i.Category, i.FirstObservedAt, GeoMath.DistanceMeters(lat, lon, i.Latitude, i.Longitude)));
        return RecurrenceEvaluator.Evaluate(category, now, priors, _o);
    }

    public static RecurrenceDto ToDto(RecurrenceResult r) => new(
        r.IsRecurring, r.Risk.ToString().ToUpperInvariant(), r.PriorCount, r.RelatedTotal, r.WindowDays, r.Summary,
        r.IsRecurring ? "RECURRING INFRASTRUCTURE ISSUE" : null,
        r.Priors.Select(p => new PriorIncidentDto(p.Id, p.Code, p.Category.ToString(), CategoryRules.Label(p.Category), p.OccurredAt, (int)p.DistanceMeters)).ToList());
}

public sealed class CorrelationService(IAppDbContext db, IOptions<ScoringOptions> options)
{
    private readonly ScoringOptions _o = options.Value;

    /// <summary>For a (new) road-damage/pothole incident, look for earlier nearby water leakage and persist a possible correlation.</summary>
    public async Task<int> DetectForTargetAsync(CivicIncident target, CancellationToken ct = default)
    {
        if (target.Category is not (IssueCategory.RoadDamage or IssueCategory.Pothole)) return 0;
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(target.Latitude, target.Longitude, _o.CorrelationRadiusMeters);
        var sources = await db.Incidents.AsNoTracking()
            .Where(i => i.Category == IssueCategory.WaterLeakage && i.Status != IncidentStatus.Rejected && i.Id != target.Id)
            .Where(i => i.Latitude >= minLat && i.Latitude <= maxLat && i.Longitude >= minLon && i.Longitude <= maxLon)
            .ToListAsync(ct);

        var created = 0;
        foreach (var s in sources)
        {
            var distance = GeoMath.DistanceMeters(s.Latitude, s.Longitude, target.Latitude, target.Longitude);
            var match = CorrelationRules.TryCorrelate(s.Category, s.FirstObservedAt, target.Category, target.FirstObservedAt, distance, _o);
            if (match is null) continue;
            if (await db.Correlations.AnyAsync(c => c.SourceIncidentId == s.Id && c.TargetIncidentId == target.Id, ct)) continue;
            db.Correlations.Add(new PotentialCorrelation
            {
                SourceIncidentId = s.Id, TargetIncidentId = target.Id, CorrelationType = match.Type,
                Confidence = match.Confidence, DistanceMeters = Math.Round(distance), Explanation = match.Explanation
            });
            created++;
        }
        if (created > 0) await db.SaveChangesAsync(ct);
        return created;
    }
}

// ============================ Trust ============================

public sealed class TrustService(IAppDbContext db)
{
    public async Task<TrustResult> CalculateAsync(Guid userId, CancellationToken ct = default)
    {
        var obs = await db.Observations.AsNoTracking().Where(o => o.UserId == userId)
            .Select(o => new { o.ValidationStatus, o.DetectedCategory, o.CitizenSelectedCategory }).ToListAsync(ct);
        var confirmations = await (from c in db.IncidentConfirmations
                                   join i in db.Incidents on c.IncidentId equals i.Id
                                   where c.UserId == userId && i.Status != IncidentStatus.Rejected
                                   select c.Id).CountAsync(ct);
        var checks = await db.CitizenVerifications.CountAsync(v => v.UserId == userId, ct);

        var input = new TrustInput(
            obs.Count(o => o.ValidationStatus == ValidationStatus.Verified),
            obs.Count(o => o.ValidationStatus == ValidationStatus.Verified && o.DetectedCategory == o.CitizenSelectedCategory),
            confirmations, checks, obs.Count(o => o.ValidationStatus == ValidationStatus.Invalid));
        return TrustScoreCalculator.Calculate(input);
    }

    public async Task RefreshAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        foreach (var id in userIds.Distinct())
        {
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) continue;
            user.TrustScore = (await CalculateAsync(id, ct)).Score;
        }
        await db.SaveChangesAsync(ct);
    }
}

// ============================ Priority (CPS) ============================

public sealed record PriorityAssessment(
    double Previous, CpsResult Result, RecurrenceResult Recurrence, ContextRiskResult Context, double EffectiveObservations);

public interface IPriorityScoringService
{
    /// <summary>Recomputes CPS from current evidence, persists the breakdown and updates the incident. Does not call SaveChanges on callers' behalf beyond its own writes.</summary>
    Task<PriorityAssessment> RecalculateAsync(CivicIncident incident, string trigger, Guid? actorId, CancellationToken ct = default);
}

public sealed class PriorityScoringService(
    IAppDbContext db, IContextRiskService context, RecurrenceService recurrence, IncidentLifecycle lifecycle,
    TimeProvider clock, IOptions<ScoringOptions> options) : IPriorityScoringService
{
    private readonly ScoringOptions _o = options.Value;

    public async Task<PriorityAssessment> RecalculateAsync(CivicIncident incident, string trigger, Guid? actorId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = incident.PriorityScore;

        // Citizen evidence: distinct observers weighted by trust (0.8x..1.2x), extra photos from the same person count 0.25.
        var observers = await (from io in db.IncidentObservations
                               join o in db.Observations on io.ObservationId equals o.Id
                               join u in db.Users on o.UserId equals u.Id
                               where io.IncidentId == incident.Id
                               select new { u.Id, u.TrustScore }).AsNoTracking().ToListAsync(ct);
        var confirmers = await (from c in db.IncidentConfirmations
                                join u in db.Users on c.UserId equals u.Id
                                where c.IncidentId == incident.Id
                                select new { u.Id, u.TrustScore }).AsNoTracking().ToListAsync(ct);

        var distinctObservers = observers.GroupBy(x => x.Id).ToList();
        double effective = distinctObservers.Sum(g => TrustScoreCalculator.EvidenceWeight(g.First().TrustScore))
                           + 0.25 * observers.Count(x => true) - 0.25 * distinctObservers.Count
                           + confirmers.Where(c => distinctObservers.All(g => g.Key != c.Id))
                                       .Sum(c => 0.6 * TrustScoreCalculator.EvidenceWeight(c.TrustScore));
        // Seeded/legacy incidents may have counts but no linked users: fall back to the stored counters.
        if (effective <= 0) effective = incident.ObservationCount + 0.6 * incident.ConfirmationCount;

        var ctx = await context.AssessAsync(incident.Category, incident.Latitude, incident.Longitude, ct);
        var rec = await recurrence.EvaluateAsync(incident.Category, incident.Latitude, incident.Longitude, incident.FirstObservedAt, incident.Id, ct);
        var ageDays = Math.Max(0, ((incident.IsActive ? now : incident.ResolvedAt ?? now) - incident.FirstObservedAt).TotalDays);

        var cps = CpsCalculator.Calculate(new CpsInput(
            incident.Category, incident.SeverityScore, effective, ctx.LocationRisk, ageDays, ctx.Exposure,
            rec.PriorCount, ctx.Reasons), _o);

        incident.PriorityScore = cps.FinalScore;
        incident.PriorityBand = cps.Band;
        incident.RecurrencePriorCount = rec.PriorCount;
        incident.UpdatedAt = now;

        db.PriorityBreakdowns.Add(new PriorityScoreBreakdown
        {
            IncidentId = incident.Id, Severity = cps.Severity, CitizenEvidence = cps.CitizenEvidence, LocationRisk = cps.LocationRisk,
            IncidentAge = cps.IncidentAge, Exposure = cps.Exposure, Recurrence = cps.Recurrence, FinalScore = cps.FinalScore,
            Band = cps.Band, ReasonsJson = JsonSerializer.Serialize(cps.Reasons), CalculatedAt = now
        });

        if (Math.Abs(previous - cps.FinalScore) >= 0.5)
            lifecycle.AddEvent(incident.Id, "PriorityChanged",
                $"CPS {(previous > 0 ? $"{previous:0} → " : "set to ")}{cps.FinalScore:0} ({CpsCalculator.BandLabel(cps.Band)}) — {trigger}",
                actorId, JsonSerializer.Serialize(new { previous, current = cps.FinalScore, trigger }));

        await db.SaveChangesAsync(ct);
        return new PriorityAssessment(previous, cps, rec, ctx, effective);
    }
}
