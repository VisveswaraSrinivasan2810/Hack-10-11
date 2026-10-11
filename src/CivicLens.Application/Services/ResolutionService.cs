using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Application.Dtos;
using CivicLens.Domain;
using CivicLens.Domain.Configuration;
using CivicLens.Domain.Entities;
using CivicLens.Domain.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CivicLens.Application.Services;

/// <summary>Proof of Resolution + Ghost Closure detection: work completed is never "resolved" until verified.</summary>
public sealed class ResolutionService(
    IAppDbContext db, IncidentLifecycle lifecycle, IResolutionVerificationService verifier, IImageStorage storage,
    INotificationService notify, TrustService trust, IPriorityScoringService priority,
    IImageProcessor imageProcessor, IOptions<ScoringOptions> options, ILogger<ResolutionService> logger)
{
    private readonly ScoringOptions _o = options.Value;
    private const double VoterRadiusMeters = 300;

    // ---------------------------------------------------------------- worker submits after-photo
    public async Task<ResolutionDto> SubmitAsync(
        Guid incidentId, ICurrentUser worker, ImageUpload after, string? notes, DateTime? completedAt, CancellationToken ct = default)
    {
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException("Incident not found.");
        if (worker.Role != UserRole.FieldWorker || worker.Id is null)
            throw new ForbiddenException("Only field workers can submit resolution evidence.");
        if (incident.AssignedWorkerId != worker.Id)
            throw new ForbiddenException("This incident is not assigned to you.");
        if (incident.Status != IncidentStatus.InProgress)
            throw new InvalidTransitionException(incident.Status is IncidentStatus.Assigned or IncidentStatus.Accepted
                ? "Start the work before submitting resolution evidence."
                : $"Resolution evidence cannot be submitted while the incident is {incident.Status}.");
        if (notes is { Length: > 1000 }) throw new BadRequestException("Resolution notes must be 1000 characters or fewer.");

        var now = lifecycle.Now;
        if (completedAt is not null && completedAt.Value.ToUniversalTime() > now.AddMinutes(5))
            throw new BadRequestException("The completion time cannot be in the future.");

        ImageTools.ValidateAndGetExtension(after.Bytes);
        var processed = imageProcessor.Process(after.Bytes);
        var ext = processed.Extension;
        var clean = processed.Bytes;
        var hash = ImageTools.Sha256Hex(clean);

        // Fraud guard: the same photo as the "before" evidence (or a previous after-photo) proves nothing.
        var beforeHashes = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                                  where io.IncidentId == incidentId select o.ImageHash).ToListAsync(ct);
        if (beforeHashes.Contains(hash))
            throw new BadRequestException("The after-photo is identical to the original evidence. Please upload a new photo of the completed work.", "duplicate_image");
        if (await db.ResolutionEvidence.AnyAsync(r => r.IncidentId == incidentId && r.AfterImageHash == hash, ct))
            throw new BadRequestException("This after-photo was already submitted for this incident.", "duplicate_image");

        var url = await storage.SaveAsync(clean, ext, ct);
        var evidence = new ResolutionEvidence
        {
            IncidentId = incidentId, WorkerId = worker.Id.Value, BeforeImageUrl = incident.PrimaryImageUrl, AfterImageUrl = url,
            AfterImageHash = hash, WorkerNotes = notes?.Trim(), CompletedAt = completedAt?.ToUniversalTime() ?? now, SubmittedAt = now
        };
        db.ResolutionEvidence.Add(evidence);

        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.IncidentId == incidentId && a.IsActive, ct);
        if (assignment is not null) assignment.CompletedAt = evidence.CompletedAt;

        lifecycle.Transition(incident, IncidentStatus.WorkCompleted, TransitionActor.Worker, worker.Id, "Work completed — after-photo uploaded");
        lifecycle.Transition(incident, IncidentStatus.AwaitingVerification, TransitionActor.System, null, "Verification requested");

        // AI before/after check. Failure of the AI provider must never break the workflow.
        ResolutionCheckResult check;
        try { check = await verifier.VerifyAsync(incident.Category, new ImageUpload(processed.AiBytes, after.FileName, processed.AiContentType), notes, ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resolution verification provider failed for incident {Incident}", incident.PublicIncidentCode);
            check = new ResolutionCheckResult(VerificationResult.Inconclusive, 0, "Original issue recorded", "Automated check unavailable",
                "Automated verification is unavailable; citizen or authority verification is required.", "unavailable");
        }
        evidence.VerificationResult = check.Result;
        evidence.AiConfidence = check.Confidence;
        evidence.AiSummary = $"{check.BeforeSummary}|{check.AfterSummary}|{check.Reason}";

        var assessment = Assess(evidence, []);
        evidence.ResolutionConfidence = assessment.Confidence;
        lifecycle.AddEvent(incidentId, "ResolutionChecked",
            $"AI verification {check.Confidence:P0} — {check.Reason}", null,
            System.Text.Json.JsonSerializer.Serialize(new { check.Result, check.Confidence, check.Provider }));

        if (assessment.Outcome == ResolutionOutcome.Disputed)
            await MarkDisputedAsync(incident, assessment, ct);
        else
        {
            await NotifyContributorsAsync(incidentId, "verification_requested", "Is this problem fixed?",
                $"Work on {incident.PublicIncidentCode} was reported complete. Please confirm whether it is actually fixed.", ct);
            await notify.NotifyRoleAsync(UserRole.Authority, "work_completed", "Work completed",
                $"{incident.PublicIncidentCode}: work completed, awaiting verification.", incidentId, ct);
        }

        await db.SaveChangesAsync(ct);
        return (await BuildDtoAsync(incidentId, ct))!;
    }

    // ---------------------------------------------------------------- citizen vote
    public async Task<ResolutionDto> VoteAsync(Guid incidentId, ICurrentUser user, VoteRequest request, CancellationToken ct = default)
    {
        if (user.Id is null) throw new UnauthorizedAppException("Please sign in.");
        if (!Enum.TryParse<CitizenVote>(request.Vote, true, out var vote))
            throw new BadRequestException("Vote must be Fixed, StillExists or Unsure.");
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException("Incident not found.");

        var recentlyResolved = incident.Status == IncidentStatus.VerifiedResolved && incident.ResolvedAt >= lifecycle.Now.AddDays(-_o.ResolvedCandidateDays);
        if (incident.Status is not (IncidentStatus.AwaitingVerification or IncidentStatus.RequiresReview) && !recentlyResolved)
            throw new ConflictException("This incident is not currently awaiting verification.");

        var evidence = await LatestEvidenceAsync(incidentId, ct) ?? throw new ConflictException("No resolution evidence has been submitted yet.");

        var isContributor = await IsContributorAsync(incidentId, user.Id.Value, ct);
        var nearby = request.Latitude is not null && request.Longitude is not null
            && GeoMath.IsValidCoordinate(request.Latitude.Value, request.Longitude.Value)
            && GeoMath.DistanceMeters(request.Latitude.Value, request.Longitude.Value, incident.Latitude, incident.Longitude) <= VoterRadiusMeters;
        if (!isContributor && !nearby)
            throw new ForbiddenException("Only citizens who reported or confirmed this incident, or who are within 300 m of it, can verify the resolution.");

        var existing = await db.CitizenVerifications.FirstOrDefaultAsync(v => v.ResolutionEvidenceId == evidence.Id && v.UserId == user.Id, ct);
        if (existing is null)
            db.CitizenVerifications.Add(new CitizenVerification
            { IncidentId = incidentId, ResolutionEvidenceId = evidence.Id, UserId = user.Id.Value, Vote = vote, CreatedAt = lifecycle.Now });
        else { existing.Vote = vote; existing.CreatedAt = lifecycle.Now; }
        await db.SaveChangesAsync(ct);

        var assessment = Assess(evidence, await LoadVotesAsync(evidence.Id, ct));
        evidence.ResolutionConfidence = assessment.Confidence;
        lifecycle.AddEvent(incidentId, "CitizenVerification",
            $"A citizen voted '{vote}' ({assessment.Citizens.Fixed} fixed / {assessment.Citizens.StillExists} still exists / {assessment.Citizens.Unsure} unsure)", user.Id);

        switch (incident.Status, assessment.Outcome)
        {
            case (IncidentStatus.AwaitingVerification, ResolutionOutcome.Verified):
                lifecycle.Transition(incident, IncidentStatus.VerifiedResolved, TransitionActor.System, null,
                    $"Verified resolved — resolution confidence {assessment.Confidence:P0}");
                await OnVerifiedAsync(incident, ct);
                break;
            case (IncidentStatus.AwaitingVerification, ResolutionOutcome.Disputed):
                await MarkDisputedAsync(incident, assessment, ct);
                break;
            case (IncidentStatus.VerifiedResolved, ResolutionOutcome.Disputed):
                lifecycle.Transition(incident, IncidentStatus.Reopened, TransitionActor.System, null,
                    $"Reopened — citizens dispute the resolution ({assessment.Reason})");
                await notify.NotifyRoleAsync(UserRole.Authority, "incident_reopened", "Incident reopened",
                    $"{incident.PublicIncidentCode} was reopened after citizens disputed the resolution.", incidentId, ct);
                break;
        }

        await db.SaveChangesAsync(ct);
        return (await BuildDtoAsync(incidentId, ct))!;
    }

    // ---------------------------------------------------------------- shared
    public ResolutionAssessment Assess(ResolutionEvidence evidence, IReadOnlyList<VoteInput> votes)
    {
        if (evidence.VerificationResult != VerificationResult.Inconclusive)
            return ResolutionConfidenceCalculator.Assess(evidence.AiConfidence, votes, _o);

        // AI unavailable: only citizens (or an authority) can decide; consensus against still disputes.
        var c = ResolutionConfidenceCalculator.Summarize(votes);
        if (c.Decisive >= 2 && c.Confidence < 0.4)
            return new(c.Confidence ?? 0, ResolutionOutcome.Disputed, c, $"{c.StillExists} of {c.Decisive} citizens report the issue still exists.");
        return new(c.Confidence ?? 0, ResolutionOutcome.Pending, c, "Automated verification unavailable; awaiting citizen or authority verification.");
    }

    private async Task MarkDisputedAsync(CivicIncident incident, ResolutionAssessment a, CancellationToken ct)
    {
        lifecycle.Transition(incident, IncidentStatus.VerificationFailed, TransitionActor.System, null, $"Verification failed — {a.Reason}");
        lifecycle.Transition(incident, IncidentStatus.RequiresReview, TransitionActor.System, null,
            $"Resolution disputed — requires review (confidence {a.Confidence:P0})");
        await notify.NotifyRoleAsync(UserRole.Authority, "resolution_disputed", "Resolution disputed — requires review",
            $"{incident.PublicIncidentCode}: verification did not confirm the fix ({a.Confidence:P0} confidence).", incident.Id, ct);
    }

    public async Task OnVerifiedAsync(CivicIncident incident, CancellationToken ct)
    {
        var obs = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                         where io.IncidentId == incident.Id select o).ToListAsync(ct);
        foreach (var o in obs.Where(o => o.ValidationStatus == ValidationStatus.Pending)) o.ValidationStatus = ValidationStatus.Verified;
        await db.SaveChangesAsync(ct);
        await trust.RefreshAsync(obs.Select(o => o.UserId), ct);
        await priority.RecalculateAsync(incident, "resolution verified", null, ct);
        await NotifyContributorsAsync(incident.Id, "incident_resolved", "Verified resolved ✓",
            $"{incident.PublicIncidentCode} has been verified as resolved. Thank you for helping.", ct);
    }

    public async Task<bool> IsContributorAsync(Guid incidentId, Guid userId, CancellationToken ct) =>
        await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
               where io.IncidentId == incidentId && o.UserId == userId select 1).AnyAsync(ct)
        || await db.IncidentConfirmations.AnyAsync(c => c.IncidentId == incidentId && c.UserId == userId, ct);

    private async Task NotifyContributorsAsync(Guid incidentId, string type, string title, string message, CancellationToken ct)
    {
        var users = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                           where io.IncidentId == incidentId select o.UserId).ToListAsync(ct);
        users.AddRange(await db.IncidentConfirmations.Where(c => c.IncidentId == incidentId).Select(c => c.UserId).ToListAsync(ct));
        foreach (var id in users.Distinct()) await notify.NotifyAsync(id, type, title, message, incidentId, ct);
    }

    private Task<ResolutionEvidence?> LatestEvidenceAsync(Guid incidentId, CancellationToken ct) =>
        db.ResolutionEvidence.Where(r => r.IncidentId == incidentId).OrderByDescending(r => r.SubmittedAt).FirstOrDefaultAsync(ct);

    private async Task<List<VoteInput>> LoadVotesAsync(Guid evidenceId, CancellationToken ct) =>
        await (from v in db.CitizenVerifications join u in db.Users on v.UserId equals u.Id
               where v.ResolutionEvidenceId == evidenceId select new VoteInput(v.Vote, u.TrustScore)).ToListAsync(ct);

    public async Task<ResolutionDto?> BuildDtoAsync(Guid incidentId, CancellationToken ct = default)
    {
        var e = await db.ResolutionEvidence.AsNoTracking().Where(r => r.IncidentId == incidentId)
            .OrderByDescending(r => r.SubmittedAt).FirstOrDefaultAsync(ct);
        if (e is null) return null;
        var incident = await db.Incidents.AsNoTracking().FirstAsync(i => i.Id == incidentId, ct);
        var votes = await LoadVotesAsync(e.Id, ct);
        var a = Assess(e, votes);

        var parts = (e.AiSummary ?? "||").Split('|');
        var before = parts.ElementAtOrDefault(0);
        var afterSummary = parts.ElementAtOrDefault(1);
        var reason = parts.ElementAtOrDefault(2);

        // Status is authoritative; assessment is explanatory.
        var (outcome, label, note) = incident.Status switch
        {
            IncidentStatus.VerifiedResolved => ("Verified", "VERIFIED RESOLVED", (string?)null),
            IncidentStatus.RequiresReview or IncidentStatus.VerificationFailed =>
                ("Disputed", "RESOLUTION DISPUTED — REQUIRES REVIEW", "Verification did not confirm the fix. This is not an accusation; the resolution simply needs a human review."),
            IncidentStatus.Reopened => ("Reopened", "REOPENED", "The resolution was disputed and the incident has been reopened."),
            _ => ("Pending", "AWAITING VERIFICATION", a.Reason)
        };

        return new ResolutionDto(
            e.Id, e.BeforeImageUrl, e.AfterImageUrl, e.WorkerNotes, e.VerificationResult.ToString(),
            (int)Math.Round(e.AiConfidence * 100), before, afterSummary, reason,
            new CitizenVerificationDto(a.Citizens.Fixed, a.Citizens.StillExists, a.Citizens.Unsure, a.Citizens.Total,
                a.Citizens.Confidence is null ? null : (int)Math.Round(a.Citizens.Confidence.Value * 100)),
            (int)Math.Round(a.Confidence * 100), outcome, label, note, e.SubmittedAt);
    }
}
