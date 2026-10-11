using System.Text.Json;
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

public sealed class IncidentService(
    IAppDbContext db, IncidentLifecycle lifecycle, IContextRiskService context, RecurrenceService recurrence,
    ResolutionService resolution, IPriorityScoringService priority, INotificationService notify, TrustService trust,
    IOptions<ScoringOptions> options)
{
    private readonly ScoringOptions _o = options.Value;

    // =============================================================== reads
    public async Task<IncidentDetailDto> GetDetailAsync(Guid id, ICurrentUser user, CancellationToken ct = default)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Incident not found.");
        if (user.Role == UserRole.FieldWorker && incident.AssignedWorkerId != user.Id)
            throw new ForbiddenException("This incident is not assigned to you.");

        var summary = (await SummariesAsync([incident], ct))[0];

        var breakdowns = await db.PriorityBreakdowns.AsNoTracking().Where(b => b.IncidentId == id)
            .OrderByDescending(b => b.CalculatedAt).Take(2).ToListAsync(ct);
        var latest = breakdowns.FirstOrDefault();
        var reasons = latest is null ? new List<string>() : JsonSerializer.Deserialize<List<string>>(latest.ReasonsJson) ?? [];
        var priorityDto = latest is null
            ? new PriorityBreakdownDto(0, 0, 0, 0, 0, 0, incident.PriorityScore, CpsCalculator.BandLabel(incident.PriorityBand), [], null, incident.UpdatedAt)
            : new PriorityBreakdownDto(latest.Severity, latest.CitizenEvidence, latest.LocationRisk, latest.IncidentAge, latest.Exposure,
                latest.Recurrence, latest.FinalScore, CpsCalculator.BandLabel(latest.Band), reasons,
                breakdowns.Count > 1 ? breakdowns[1].FinalScore : null, latest.CalculatedAt, latest.ContextAmplifier);

        var ctx = await context.AssessAsync(incident.Category, incident.Latitude, incident.Longitude, ct);

        var observations = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                                  where io.IncidentId == id orderby o.CreatedAt
                                  select new ObservationPublicDto(o.Id, o.ImageUrl, o.CreatedAt, o.CitizenSelectedCategory.ToString(),
                                      (int)Math.Round(o.AiConfidence * 100), (int)Math.Round(io.SameIncidentScore * 100), io.MergeReason,
                                      user.Id != null && o.UserId == user.Id, o.SuspectedDuplicateImage)).AsNoTracking().ToListAsync(ct);

        var timeline = (await db.History.AsNoTracking().Where(h => h.IncidentId == id).OrderBy(h => h.CreatedAt).ToListAsync(ct))
            .Select(h => new TimelineItemDto(h.CreatedAt, h.EventType, h.Message ?? h.EventType, h.OldStatus?.ToString(), h.NewStatus?.ToString())).ToList();

        var rec = await recurrence.EvaluateAsync(incident.Category, incident.Latitude, incident.Longitude, incident.FirstObservedAt, id, ct);
        var memory = await MemoryAsync(incident.Latitude, incident.Longitude, _o.RecurrenceRadiusMeters, id, ct);
        var correlations = await CorrelationsForAsync(id, ct);
        var resolutionDto = await resolution.BuildDtoAsync(id, ct);

        var isContributor = user.Id is not null && await resolution.IsContributorAsync(id, user.Id.Value, ct);
        var canConfirm = user.Role == UserRole.Citizen && incident.IsActive && !isContributor
                         && incident.Status is not (IncidentStatus.AwaitingVerification or IncidentStatus.WorkCompleted);
        var recentlyResolved = incident.Status == IncidentStatus.VerifiedResolved && incident.ResolvedAt >= lifecycle.Now.AddDays(-_o.ResolvedCandidateDays);
        var canVerify = user.Role == UserRole.Citizen && resolutionDto is not null
                        && (incident.Status is IncidentStatus.AwaitingVerification or IncidentStatus.RequiresReview || recentlyResolved);
        string? myVote = null;
        if (user.Id is not null && resolutionDto is not null)
            myVote = (await db.CitizenVerifications.AsNoTracking()
                .Where(v => v.ResolutionEvidenceId == resolutionDto.EvidenceId && v.UserId == user.Id).FirstOrDefaultAsync(ct))?.Vote.ToString();

        IReadOnlyList<string> next = user.Role switch
        {
            UserRole.Authority => IncidentStateMachine.NextStates(incident.Status, TransitionActor.Authority)
                .Where(s => s != IncidentStatus.Assigned).Select(s => s.ToString()).ToList(),
            UserRole.FieldWorker when incident.AssignedWorkerId == user.Id => IncidentStateMachine.NextStates(incident.Status, TransitionActor.Worker)
                .Where(s => s != IncidentStatus.WorkCompleted).Select(s => s.ToString()).ToList(),
            _ => []
        };

        var banner = incident.Status switch
        {
            IncidentStatus.RequiresReview or IncidentStatus.VerificationFailed => "RESOLUTION DISPUTED — REQUIRES REVIEW",
            IncidentStatus.AwaitingVerification or IncidentStatus.WorkCompleted => "AWAITING VERIFICATION",
            IncidentStatus.VerifiedResolved => "VERIFIED RESOLVED",
            IncidentStatus.Reopened => "REOPENED",
            _ => incident.Status.ToString().ToUpperInvariant()
        };

        return new IncidentDetailDto(summary, incident.SeverityScore, priorityDto, ObservationService.ToContextDto(ctx), observations, timeline,
            RecurrenceService.ToDto(rec), memory, correlations, resolutionDto, next, canConfirm, canVerify, myVote, isContributor, banner);
    }

    public async Task<PagedResult<IncidentSummaryDto>> ListAsync(IncidentListQuery q, ICurrentUser user, CancellationToken ct = default)
    {
        var query = db.Incidents.AsNoTracking().AsQueryable();

        if (user.Role == UserRole.FieldWorker) query = query.Where(i => i.AssignedWorkerId == user.Id);
        else if (q.AssignedToMe == true) query = query.Where(i => i.AssignedWorkerId == user.Id);
        else if (q.AssignedWorkerId is not null) query = query.Where(i => i.AssignedWorkerId == q.AssignedWorkerId);

        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            if (!Enum.TryParse<IncidentStatus>(q.Status, true, out var st)) throw new BadRequestException("Unknown status filter.");
            query = query.Where(i => i.Status == st);
        }
        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            if (!Enum.TryParse<IssueCategory>(q.Category, true, out var cat)) throw new BadRequestException("Unknown category filter.");
            query = query.Where(i => i.Category == cat);
        }
        if (!string.IsNullOrWhiteSpace(q.Band))
        {
            if (!Enum.TryParse<PriorityBand>(q.Band.Replace(" ", ""), true, out var band)) throw new BadRequestException("Unknown priority band filter.");
            query = query.Where(i => i.PriorityBand == band);
        }
        if (q.MinPriority is not null) query = query.Where(i => i.PriorityScore >= q.MinPriority);
        if (q.ActiveOnly == true) query = query.Where(i => i.Status != IncidentStatus.VerifiedResolved && i.Status != IncidentStatus.Rejected);
        if (q.Recurring == true) query = query.Where(i => i.RecurrencePriorCount >= _o.RecurringMinPriorIncidents);
        if (q.Disputed == true) query = query.Where(i => i.ResolutionStatus == ResolutionStatus.Disputed);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(i => i.PublicIncidentCode.ToLower().Contains(s) || (i.LocationLabel != null && i.LocationLabel.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        query = q.Sort?.ToLowerInvariant() switch
        {
            "newest" => query.OrderByDescending(i => i.FirstObservedAt),
            "oldest" => query.OrderBy(i => i.FirstObservedAt),
            _ => query.OrderByDescending(i => i.PriorityScore).ThenBy(i => i.FirstObservedAt)
        };
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);
        var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<IncidentSummaryDto>(await SummariesAsync(items, ct), total, page, size);
    }

    public async Task<IReadOnlyList<IncidentSummaryDto>> NearbyAsync(double lat, double lon, double radiusMeters, CancellationToken ct = default)
    {
        if (!GeoMath.IsValidCoordinate(lat, lon)) throw new BadRequestException("Invalid location.", "invalid_location");
        radiusMeters = Math.Clamp(radiusMeters, 50, 20_000);
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(lat, lon, radiusMeters);
        var cutoff = lifecycle.Now.AddDays(-_o.ResolvedCandidateDays);
        var list = await db.Incidents.AsNoTracking()
            .Where(i => i.Latitude >= minLat && i.Latitude <= maxLat && i.Longitude >= minLon && i.Longitude <= maxLon)
            .Where(i => i.Status != IncidentStatus.Rejected && (i.Status != IncidentStatus.VerifiedResolved || i.ResolvedAt >= cutoff))
            .ToListAsync(ct);
        var within = list.Select(i => (i, d: GeoMath.DistanceMeters(lat, lon, i.Latitude, i.Longitude))).Where(x => x.d <= radiusMeters)
            .OrderBy(x => x.d).Take(200).ToList();
        var summaries = await SummariesAsync(within.Select(x => x.i).ToList(), ct);
        return summaries.Select((s, idx) => s with { DistanceMeters = (int)Math.Round(within[idx].d) }).ToList();
    }

    public async Task<IReadOnlyList<ObservationPublicDto>> MatchesAsync(Guid id, ICurrentUser user, CancellationToken ct = default)
    {
        if (!await db.Incidents.AnyAsync(i => i.Id == id, ct)) throw new NotFoundException("Incident not found.");
        return await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                      where io.IncidentId == id orderby o.CreatedAt
                      select new ObservationPublicDto(o.Id, o.ImageUrl, o.CreatedAt, o.CitizenSelectedCategory.ToString(),
                          (int)Math.Round(o.AiConfidence * 100), (int)Math.Round(io.SameIncidentScore * 100), io.MergeReason,
                          user.Id != null && o.UserId == user.Id, o.SuspectedDuplicateImage)).AsNoTracking().ToListAsync(ct);
    }

    /// <summary>Infrastructure Memory: everything that ever happened around a location, oldest first.</summary>
    public async Task<IReadOnlyList<MemoryItemDto>> MemoryAsync(double lat, double lon, double radiusMeters, Guid? currentId, CancellationToken ct = default)
    {
        if (!GeoMath.IsValidCoordinate(lat, lon)) throw new BadRequestException("Invalid location.", "invalid_location");
        radiusMeters = Math.Clamp(radiusMeters, 10, 1000);
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(lat, lon, radiusMeters);
        var list = await db.Incidents.AsNoTracking()
            .Where(i => i.Status != IncidentStatus.Rejected)
            .Where(i => i.Latitude >= minLat && i.Latitude <= maxLat && i.Longitude >= minLon && i.Longitude <= maxLon)
            .ToListAsync(ct);
        return list.Where(i => GeoMath.DistanceMeters(lat, lon, i.Latitude, i.Longitude) <= radiusMeters)
            .OrderBy(i => i.FirstObservedAt)
            .Select(i => new MemoryItemDto(i.Id, i.PublicIncidentCode, i.Category.ToString(), CategoryRules.Label(i.Category), i.FirstObservedAt,
                i.Status.ToString(), i.Id == currentId)).ToList();
    }

    public async Task<IReadOnlyList<CorrelationDto>> CorrelationsForAsync(Guid? incidentId, CancellationToken ct = default)
    {
        var q = db.Correlations.AsNoTracking().AsQueryable();
        if (incidentId is not null) q = q.Where(c => c.SourceIncidentId == incidentId || c.TargetIncidentId == incidentId);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Take(50).ToListAsync(ct);
        var ids = rows.SelectMany(r => new[] { r.SourceIncidentId, r.TargetIncidentId }).Distinct().ToList();
        var inc = await db.Incidents.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return rows.Where(r => inc.ContainsKey(r.SourceIncidentId) && inc.ContainsKey(r.TargetIncidentId)).Select(r =>
        {
            var s = inc[r.SourceIncidentId]; var t = inc[r.TargetIncidentId];
            return new CorrelationDto(r.Id, s.Id, s.PublicIncidentCode, CategoryRules.Label(s.Category), s.FirstObservedAt,
                t.Id, t.PublicIncidentCode, CategoryRules.Label(t.Category), t.FirstObservedAt, (int)r.DistanceMeters,
                CorrelationRules.Label(r.Confidence), r.Confidence, r.Explanation);
        }).ToList();
    }

    public async Task<IReadOnlyList<WorkerDto>> WorkersAsync(CancellationToken ct = default)
    {
        var workers = await db.Users.AsNoTracking().Where(u => u.Role == UserRole.FieldWorker).OrderBy(u => u.Name).ToListAsync(ct);
        var counts = await db.Incidents.AsNoTracking()
            .Where(i => i.AssignedWorkerId != null && i.Status != IncidentStatus.VerifiedResolved && i.Status != IncidentStatus.Rejected)
            .GroupBy(i => i.AssignedWorkerId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return workers.Select(w => new WorkerDto(w.Id, w.Name, counts.FirstOrDefault(c => c.Key == w.Id)?.N ?? 0)).ToList();
    }

    // =============================================================== commands
    /// <summary>Citizen "I see this too" — adds corroboration without a photo.</summary>
    public async Task<IncidentSummaryDto> ConfirmAsync(Guid id, ICurrentUser user, CancellationToken ct = default)
    {
        if (user.Id is null || user.Role != UserRole.Citizen) throw new ForbiddenException("Only citizens can confirm incidents.");
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Incident not found.");
        if (!incident.IsActive) throw new ConflictException("This incident is already closed.");
        if (incident.Status is IncidentStatus.AwaitingVerification or IncidentStatus.WorkCompleted)
            throw new ConflictException("Work on this incident is complete. Please verify the resolution instead.");
        if (await resolution.IsContributorAsync(id, user.Id.Value, ct))
            throw new ConflictException("You have already contributed to this incident.");

        var before = incident.PriorityScore;
        db.IncidentConfirmations.Add(new IncidentConfirmation { IncidentId = id, UserId = user.Id.Value, CreatedAt = lifecycle.Now });
        incident.ConfirmationCount++;
        lifecycle.AddEvent(id, "CitizenConfirmed", $"A citizen confirmed the incident ({incident.ConfirmationCount} confirmations)", user.Id);
        await db.SaveChangesAsync(ct);
        await priority.RecalculateAsync(incident, "citizen confirmation", user.Id, ct);
        await notify.NotifyAsync(user.Id.Value, "observation_merged", "Confirmation recorded",
            $"Thanks for confirming {incident.PublicIncidentCode}.", id, ct);
        await trust.RefreshAsync([user.Id.Value], ct);
        return (await SummariesAsync([incident], ct))[0];
    }

    public async Task<IncidentSummaryDto> AssignAsync(Guid id, AssignRequest req, ICurrentUser user, CancellationToken ct = default)
    {
        if (user.Role != UserRole.Authority || user.Id is null) throw new ForbiddenException("Only authorities can assign incidents.");
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Incident not found.");
        var worker = await db.Users.FirstOrDefaultAsync(u => u.Id == req.WorkerId, ct);
        if (worker is null || worker.Role != UserRole.FieldWorker) throw new BadRequestException("The selected field worker is not available.");
        if (incident.Status is IncidentStatus.VerifiedResolved or IncidentStatus.Rejected or IncidentStatus.AwaitingVerification
            or IncidentStatus.WorkCompleted or IncidentStatus.VerificationFailed)
            throw new InvalidTransitionException($"An incident that is {incident.Status} cannot be assigned.");

        var wasAssigned = incident.AssignedWorkerId is not null;
        foreach (var a in await db.Assignments.Where(a => a.IncidentId == id && a.IsActive).ToListAsync(ct)) a.IsActive = false;
        db.Assignments.Add(new IncidentAssignment { IncidentId = id, WorkerId = worker.Id, AssignedBy = user.Id.Value, AssignedAt = lifecycle.Now });
        incident.AssignedWorkerId = worker.Id;

        var message = $"{(wasAssigned ? "Reassigned" : "Assigned")} to {worker.Name}{(string.IsNullOrWhiteSpace(req.Note) ? "" : $" — {req.Note.Trim()}")}";
        if (incident.Status == IncidentStatus.Assigned) lifecycle.AddEvent(id, "Reassigned", message, user.Id);
        else lifecycle.Transition(incident, IncidentStatus.Assigned, TransitionActor.Authority, user.Id, message);

        await ValidateObservationsAsync(incident, ct);
        await db.SaveChangesAsync(ct);
        await notify.NotifyAsync(worker.Id, "incident_assigned", "New assignment",
            $"{incident.PublicIncidentCode} ({CategoryRules.Label(incident.Category)}) was assigned to you.", id, ct);
        return (await SummariesAsync([incident], ct))[0];
    }

    public async Task<IncidentSummaryDto> ChangeStatusAsync(Guid id, StatusChangeRequest req, ICurrentUser user, CancellationToken ct = default)
    {
        if (user.Id is null || user.Role is null || user.Role == UserRole.Citizen)
            throw new ForbiddenException("You are not allowed to change incident status.");
        if (!Enum.TryParse<IncidentStatus>(req.Status, true, out var to)) throw new BadRequestException("Unknown status.");
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Incident not found.");
        var reason = req.Reason?.Trim();

        TransitionActor actor;
        if (user.Role == UserRole.FieldWorker)
        {
            if (incident.AssignedWorkerId != user.Id) throw new ForbiddenException("This incident is not assigned to you.");
            if (to == IncidentStatus.WorkCompleted)
                throw new BadRequestException("Upload resolution evidence to complete the work.");
            actor = TransitionActor.Worker;
        }
        else
        {
            actor = TransitionActor.Authority;
            if (to == IncidentStatus.Assigned) throw new BadRequestException("Use the assign action to choose a field worker.");
            if (to is IncidentStatus.Rejected or IncidentStatus.Reopened or IncidentStatus.VerifiedResolved && string.IsNullOrWhiteSpace(reason))
                throw new BadRequestException("A reason is required for this change; it is recorded in the audit trail.");
        }

        var manual = actor == TransitionActor.Authority && to == IncidentStatus.VerifiedResolved;
        lifecycle.Transition(incident, to, actor, user.Id,
            manual ? $"Manually verified resolved by authority — {reason}" : $"Status changed to {to}{(reason is null ? "" : $" — {reason}")}",
            JsonSerializer.Serialize(new { reason, manual }));

        switch (to)
        {
            case IncidentStatus.Validated: await ValidateObservationsAsync(incident, ct); break;
            case IncidentStatus.Accepted:
                var a = await db.Assignments.FirstOrDefaultAsync(x => x.IncidentId == id && x.IsActive, ct);
                if (a is not null) a.AcceptedAt = lifecycle.Now;
                break;
            case IncidentStatus.InProgress:
                await NotifyContributorsAsync(id, "work_started", "Work started", $"Work has started on {incident.PublicIncidentCode}.", ct);
                break;
            case IncidentStatus.Rejected:
                await InvalidateObservationsAsync(incident, ct); break;
        }
        if (to == IncidentStatus.Reopened)
        {
            await notify.NotifyRoleAsync(UserRole.Authority, "incident_reopened", "Incident reopened", $"{incident.PublicIncidentCode} was reopened.", id, ct);
            foreach (var old in await db.Assignments.Where(x => x.IncidentId == id && x.IsActive).ToListAsync(ct)) old.IsActive = false;
            incident.AssignedWorkerId = null;
        }
        await db.SaveChangesAsync(ct);
        if (manual) await resolution.OnVerifiedAsync(incident, ct);
        return (await SummariesAsync([incident], ct))[0];
    }

    /// <summary>Human-in-the-loop: authority corrects AI category / severity. Always audited.</summary>
    public async Task<IncidentSummaryDto> OverrideAsync(Guid id, OverrideRequest req, ICurrentUser user, CancellationToken ct = default)
    {
        if (user.Role != UserRole.Authority || user.Id is null) throw new ForbiddenException("Only authorities can override AI results.");
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new BadRequestException("A reason is required for an override.");
        if (req.Category is null && req.Severity is null) throw new BadRequestException("Provide a category or a severity to override.");
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Incident not found.");
        if (!incident.IsActive) throw new ConflictException("A closed incident cannot be overridden.");

        if (req.Category is not null)
        {
            if (!Enum.TryParse<IssueCategory>(req.Category, true, out var cat) || cat == IssueCategory.NoIssue)
                throw new BadRequestException("Unknown category.");
            if (cat != incident.Category)
            {
                lifecycle.AddEvent(id, "AuthorityOverrideCategory",
                    $"Authority changed category {CategoryRules.Label(incident.Category)} → {CategoryRules.Label(cat)} — {req.Reason.Trim()}", user.Id,
                    JsonSerializer.Serialize(new { from = incident.Category, to = cat, reason = req.Reason }));
                incident.Category = cat;
            }
        }
        if (req.Severity is not null)
        {
            if (req.Severity is < 0 or > 100) throw new BadRequestException("Severity must be between 0 and 100.");
            lifecycle.AddEvent(id, "AuthorityOverrideSeverity",
                $"Authority changed severity {incident.SeverityScore} → {req.Severity} — {req.Reason.Trim()}", user.Id,
                JsonSerializer.Serialize(new { from = incident.SeverityScore, to = req.Severity, reason = req.Reason }));
            incident.SeverityScore = req.Severity.Value;
            incident.SeverityOverridden = true;
        }
        await db.SaveChangesAsync(ct);
        await priority.RecalculateAsync(incident, "authority override", user.Id, ct);
        return (await SummariesAsync([incident], ct))[0];
    }

    // =============================================================== helpers
    private async Task ValidateObservationsAsync(CivicIncident incident, CancellationToken ct)
    {
        var pending = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                             where io.IncidentId == incident.Id && o.ValidationStatus == ValidationStatus.Pending select o).ToListAsync(ct);
        foreach (var o in pending) o.ValidationStatus = ValidationStatus.Verified;
        await db.SaveChangesAsync(ct);
        await trust.RefreshAsync(pending.Select(o => o.UserId), ct);
    }

    private async Task InvalidateObservationsAsync(CivicIncident incident, CancellationToken ct)
    {
        var obs = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                         where io.IncidentId == incident.Id select o).ToListAsync(ct);
        foreach (var o in obs) o.ValidationStatus = ValidationStatus.Invalid;
        await db.SaveChangesAsync(ct);
        await trust.RefreshAsync(obs.Select(o => o.UserId), ct);
        foreach (var uid in obs.Select(o => o.UserId).Distinct())
            await notify.NotifyAsync(uid, "observation_rejected", "Report not accepted",
                $"{incident.PublicIncidentCode} was reviewed and not accepted as a valid civic issue.", incident.Id, ct);
    }

    private async Task NotifyContributorsAsync(Guid incidentId, string type, string title, string message, CancellationToken ct)
    {
        var users = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                           where io.IncidentId == incidentId select o.UserId).ToListAsync(ct);
        users.AddRange(await db.IncidentConfirmations.Where(c => c.IncidentId == incidentId).Select(c => c.UserId).ToListAsync(ct));
        foreach (var id in users.Distinct()) await notify.NotifyAsync(id, type, title, message, incidentId, ct);
    }

    public async Task<List<IncidentSummaryDto>> SummariesAsync(IReadOnlyList<CivicIncident> incidents, CancellationToken ct = default)
    {
        var workerIds = incidents.Where(i => i.AssignedWorkerId != null).Select(i => i.AssignedWorkerId!.Value).Distinct().ToList();
        var workers = await db.Users.AsNoTracking().Where(u => workerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var zones = await db.Zones.AsNoTracking().Select(z => new { z.Id, z.Name }).ToDictionaryAsync(z => z.Id, z => z.Name, ct);
        var now = lifecycle.Now;
        return incidents.Select(i => new IncidentSummaryDto(
            i.Id, i.PublicIncidentCode, i.Category.ToString(), CategoryRules.Label(i.Category), i.Latitude, i.Longitude, i.LocationLabel,
            i.PriorityScore, CpsCalculator.BandLabel(i.PriorityBand), i.Status.ToString(), i.ResolutionStatus.ToString(),
            i.ObservationCount, i.ConfirmationCount, i.FirstObservedAt, i.UpdatedAt,
            Math.Round(((i.IsActive ? now : i.ResolvedAt ?? now) - i.FirstObservedAt).TotalHours, 1),
            i.AssignedWorkerId is { } w && workers.TryGetValue(w, out var n) ? n : null, i.AssignedWorkerId,
            i.RecurrencePriorCount >= _o.RecurringMinPriorIncidents, i.RecurrencePriorCount, i.PrimaryImageUrl,
            i.ZoneId is { } z && zones.TryGetValue(z, out var zn) ? zn : null, null)).ToList();
    }
}
