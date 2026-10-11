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

public sealed record AnalyzeRequest(
    ImageUpload Image, double Latitude, double Longitude, string? Description,
    double? DeviceLatitude, double? DeviceLongitude, string? LocationLabel);

/// <summary>SEE → SNAP → SUBMIT: analyze a photo, rank duplicate incidents, then create or merge.</summary>
public sealed class ObservationService(
    IAppDbContext db, IIssueClassificationService classifier, IImageSimilarityService similarity, IImageStorage storage,
    IContextRiskService context, IIncidentMatchingService matching, RecurrenceService recurrence, CorrelationService correlation,
    IPriorityScoringService priority, IncidentLifecycle lifecycle, INotificationService notify, TrustService trust,
    IImageProcessor imageProcessor, IGeocoder geocoder, PoiCoverageService poiCoverage, IOptions<ScoringOptions> options)
{
    private readonly ScoringOptions _o = options.Value;
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromHours(2);

    // ------------------------------------------------------------------ analyze
    public async Task<AnalysisResponse> AnalyzeAsync(Guid userId, AnalyzeRequest req, CancellationToken ct = default)
    {
        var now = lifecycle.Now;
        ValidateLocation(req.Latitude, req.Longitude, req.DeviceLatitude, req.DeviceLongitude);
        if (req.Description is { Length: > 500 }) throw new BadRequestException("The description must be 500 characters or fewer.");

        ImageTools.ValidateAndGetExtension(req.Image.Bytes); // fast reject of non-images before any decoding
        var processed = imageProcessor.Process(req.Image.Bytes);
        var ext = processed.Extension;
        var clean = processed.Bytes;
        var hash = ImageTools.Sha256Hex(clean);

        var drafts = await db.ObservationDrafts.CountAsync(d => d.UserId == userId && d.CreatedAt >= now.AddHours(-1), ct);
        if (drafts >= _o.MaxObservationsPerHour * 3)
            throw new RateLimitException("You're analysing photos very quickly. Please wait a little before trying again.");

        if (await db.Observations.AnyAsync(o => o.UserId == userId && o.ImageHash == hash, ct))
            throw new ConflictException("You have already submitted this exact photo. Please take a new photo of the issue.");
        var reusedByOthers = await db.Observations.AnyAsync(o => o.UserId != userId && o.ImageHash == hash, ct);

        var warnings = new List<string>();
        if (reusedByOthers) warnings.Add("This photo matches one submitted earlier by another citizen. It will be flagged for review.");

        ClassificationResult cls;
        try { cls = await classifier.ClassifyAsync(new ImageUpload(processed.AiBytes, req.Image.FileName, processed.AiContentType), req.Description, ct); }
        catch (Exception)
        {
            // AI unavailable: never block the citizen — ask them to choose the category manually.
            warnings.Add("Automatic analysis is unavailable right now. Please choose the category yourself.");
            cls = new ClassificationResult(IssueCategory.Other, 0, 40,
                Enum.GetValues<IssueCategory>().Where(c => c != IssueCategory.NoIssue).Select(c => new CategoryProbability(c, 0)).ToList(),
                "Automatic analysis unavailable.", "unavailable");
        }
        if (cls.Category == IssueCategory.NoIssue)
        {
            warnings.Add("We could not see a civic issue in this photo. Please check the photo or choose a category.");
            cls = cls with { Category = IssueCategory.Other, Confidence = Math.Min(cls.Confidence, 0.3) };
        }
        var lowConfidence = cls.Confidence < _o.MinClassificationConfidence;
        if (lowConfidence) warnings.Add("We're not fully sure what this is. Please select the correct category.");

        var url = await storage.SaveAsync(clean, ext, ct);
        var signature = similarity.ComputeSignature(processed.AiBytes);
        await poiCoverage.EnsureAsync(req.Latitude, req.Longitude, ct); // makes contextual risk work anywhere, not just the demo city
        var label = await ResolveLocationLabelAsync(req.Latitude, req.Longitude, req.LocationLabel, ct);

        var draft = new ObservationDraft
        {
            UserId = userId, ImageUrl = url, ImageHash = hash, ImageSignature = signature, Latitude = req.Latitude, Longitude = req.Longitude,
            Description = req.Description?.Trim(), LocationLabel = label, DetectedCategory = cls.Category, AiConfidence = cls.Confidence,
            SeverityScore = cls.Severity, AiReason = cls.Reason, SuspectedDuplicateImage = reusedByOthers, CreatedAt = now
        };
        db.ObservationDrafts.Add(draft);
        await db.SaveChangesAsync(ct);

        var ctx = await context.AssessAsync(cls.Category, req.Latitude, req.Longitude, ct);
        var matches = await MatchesForAsync(draft, cls.Category, ct);
        var rec = await recurrence.EvaluateAsync(cls.Category, req.Latitude, req.Longitude, now, null, ct);

        var alternatives = cls.Alternatives.Where(a => a.Probability > 0).Take(3)
            .Select(a => new CategoryOptionDto(a.Category.ToString(), CategoryRules.Label(a.Category), (int)Math.Round(a.Probability * 100))).ToList();

        return new AnalysisResponse(
            draft.Id, url, cls.Category.ToString(), CategoryRules.Label(cls.Category), (int)Math.Round(cls.Confidence * 100), lowConfidence,
            alternatives, cls.Severity, SeverityLabel(cls.Severity), cls.Reason, cls.Provider, label, ToContextDto(ctx), matches,
            rec.PriorCount > 0 ? RecurrenceService.ToDto(rec) : null, warnings);
    }

    /// <summary>Re-rank duplicates after the citizen edits the category.</summary>
    public async Task<IReadOnlyList<IncidentMatchDto>> RematchAsync(Guid userId, Guid draftId, IssueCategory category, CancellationToken ct = default)
    {
        var draft = await LoadDraftAsync(userId, draftId, ct);
        return await MatchesForAsync(draft, category, ct);
    }

    // ------------------------------------------------------------------ submit
    public async Task<SubmitObservationResult> SubmitAsync(Guid userId, SubmitObservationRequest req, CancellationToken ct = default)
    {
        var now = lifecycle.Now;
        var draft = await LoadDraftAsync(userId, req.DraftId, ct);

        var sentInLastHour = await db.Observations.CountAsync(o => o.UserId == userId && o.CreatedAt >= now.AddHours(-1), ct);
        if (sentInLastHour >= _o.MaxObservationsPerHour)
            throw new RateLimitException($"You can submit up to {_o.MaxObservationsPerHour} observations per hour. Please try again later.");

        IssueCategory category = draft.DetectedCategory;
        if (!string.IsNullOrWhiteSpace(req.SelectedCategory))
        {
            if (!Enum.TryParse(req.SelectedCategory, true, out category) || category == IssueCategory.NoIssue)
                throw new BadRequestException("Unknown issue category.");
        }
        else if (draft.AiConfidence < _o.MinClassificationConfidence)
            throw new BadRequestException("We're not fully sure what this is. Please select the correct category.", "category_required");

        var action = req.Action?.Trim().ToLowerInvariant();
        if (action is not ("createnew" or "attachtoexisting"))
            throw new BadRequestException("Action must be CreateNew or AttachToExisting.");
        if (req.Description is { Length: > 500 }) throw new BadRequestException("The description must be 500 characters or fewer.");

        var matches = await matching.FindMatchesAsync(draft.Latitude, draft.Longitude, category, draft.ImageSignature, draft.ImageHash, now, ct);

        var observation = new Observation
        {
            Id = draft.Id, // same id as the draft: a concurrent double-submit hits the primary key instead of creating two observations
            UserId = userId, ImageUrl = draft.ImageUrl, ImageHash = draft.ImageHash, ImageSignature = draft.ImageSignature,
            Latitude = draft.Latitude, Longitude = draft.Longitude, Location = GeoMath.ToPoint(draft.Latitude, draft.Longitude),
            Description = (req.Description ?? draft.Description)?.Trim(), DetectedCategory = draft.DetectedCategory, AiConfidence = draft.AiConfidence,
            CitizenSelectedCategory = category, SeverityScore = draft.SeverityScore, AiReason = draft.AiReason,
            SuspectedDuplicateImage = draft.SuspectedDuplicateImage, CreatedAt = now
        };
        db.Observations.Add(observation);
        draft.Consumed = true;

        CivicIncident incident;
        bool isNew = action == "createnew", reopened = false, overrode = false;
        double sameScore = 1;
        string mergeReason = "NewIncident";
        int countBefore = 0;
        double priorityBefore = 0;

        if (isNew)
        {
            overrode = matches.Any(m => m.Score.Verdict == MatchVerdict.VeryLikelySame);
            incident = await CreateIncidentAsync(draft, observation, category, now, ct);
            if (overrode)
                lifecycle.AddEvent(incident.Id, "CitizenOverrodeMatch",
                    $"Citizen created a separate incident although {matches.First().Incident.PublicIncidentCode} was a likely match ({matches.First().Score.Total:P0})", userId);
        }
        else
        {
            if (req.IncidentId is null) throw new BadRequestException("Choose the incident to attach this observation to.");
            incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == req.IncidentId, ct) ?? throw new NotFoundException("Incident not found.");
            if (incident.Status == IncidentStatus.Rejected) throw new ConflictException("That incident was rejected and cannot receive new observations.");

            var distance = GeoMath.DistanceMeters(draft.Latitude, draft.Longitude, incident.Latitude, incident.Longitude);
            if (distance > _o.MatchRadiusMeters * 5)
                throw new BadRequestException($"That incident is {distance:0} m away — too far to be the same issue. Create a new incident instead.");

            var match = matches.FirstOrDefault(m => m.Incident.Id == incident.Id);
            sameScore = match?.Score.Total ?? Math.Round(SameIncidentScorer.Score(distance, category, incident.Category, 0, 0, _o).Total, 4);
            mergeReason = match is null ? "CitizenOverride"
                : match.Score.Verdict == MatchVerdict.VeryLikelySame ? "CitizenConfirmedHighConfidence" : "CitizenConfirmedPossibleMatch";

            countBefore = incident.ObservationCount;
            priorityBefore = incident.PriorityScore;

            db.IncidentObservations.Add(new IncidentObservation
            { IncidentId = incident.Id, ObservationId = observation.Id, SameIncidentScore = sameScore, MergeReason = mergeReason, CreatedAt = now });
            incident.ObservationCount++;
            incident.LastObservedAt = now;
            // Replace a seeded illustration when a citizen attaches uploaded evidence.
            if (string.IsNullOrWhiteSpace(incident.PrimaryImageUrl) || incident.PrimaryImageUrl.StartsWith("/demo/", StringComparison.Ordinal))
                incident.PrimaryImageUrl = observation.ImageUrl;
            if (!incident.SeverityOverridden) incident.SeverityScore = Math.Max(incident.SeverityScore, observation.SeverityScore);

            lifecycle.AddEvent(incident.Id, "ObservationMerged",
                $"Observation #{incident.ObservationCount} merged (same-incident confidence {sameScore:P0})", userId,
                JsonSerializer.Serialize(new { sameScore, mergeReason, distanceMeters = Math.Round(distance) }));

            if (incident.Status == IncidentStatus.VerifiedResolved)
            {
                lifecycle.Transition(incident, IncidentStatus.Reopened, TransitionActor.System, userId,
                    "Reopened — a new observation was received for a resolved incident");
                reopened = true;
            }
            await db.SaveChangesAsync(ct);
        }

        var assessment = await priority.RecalculateAsync(incident, isNew ? "incident created" : "new observation merged", userId, ct);
        if (isNew) await correlation.DetectForTargetAsync(incident, ct);

        // Corroboration: a second independent citizen makes every pending observation verified.
        var userIds = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                             where io.IncidentId == incident.Id select o.UserId).Distinct().ToListAsync(ct);
        if (userIds.Count >= 2)
        {
            var pending = await (from io in db.IncidentObservations join o in db.Observations on io.ObservationId equals o.Id
                                 where io.IncidentId == incident.Id && o.ValidationStatus == ValidationStatus.Pending select o).ToListAsync(ct);
            foreach (var o in pending) o.ValidationStatus = ValidationStatus.Verified;
            await db.SaveChangesAsync(ct);
            await trust.RefreshAsync(userIds, ct);
        }

        // Notifications (in-app)
        if (isNew)
        {
            await notify.NotifyAsync(userId, "observation_accepted", "Observation accepted",
                $"Your report created incident {incident.PublicIncidentCode} (priority {incident.PriorityScore:0}).", incident.Id, ct);
            if (incident.PriorityBand >= PriorityBand.VeryHigh)
                await notify.NotifyRoleAsync(UserRole.Authority, "priority_high", $"{CpsCalculator.BandLabel(incident.PriorityBand)} incident",
                    $"{incident.PublicIncidentCode} {CategoryRules.Label(incident.Category)} — CPS {incident.PriorityScore:0}.", incident.Id, ct);
        }
        else
        {
            await notify.NotifyAsync(userId, "observation_merged", "Observation merged",
                $"Your observation was added to {incident.PublicIncidentCode} ({incident.ObservationCount} observations).", incident.Id, ct);
            if (Math.Abs(incident.PriorityScore - priorityBefore) >= 10)
                foreach (var id in userIds.Where(u => u != userId))
                    await notify.NotifyAsync(id, "priority_changed", "Priority increased",
                        $"{incident.PublicIncidentCode} priority changed {priorityBefore:0} → {incident.PriorityScore:0}.", incident.Id, ct);
            if (reopened)
                await notify.NotifyRoleAsync(UserRole.Authority, "incident_reopened", "Incident reopened",
                    $"{incident.PublicIncidentCode} was reopened by a new citizen observation.", incident.Id, ct);
        }

        await db.SaveChangesAsync(ct);

        var message = isNew ? $"Incident {incident.PublicIncidentCode} created."
            : reopened ? $"Merged into {incident.PublicIncidentCode} — the incident was reopened."
            : $"Merged into {incident.PublicIncidentCode}. {incident.ObservationCount} citizen observations now support it.";
        return new SubmitObservationResult(
            incident.Id, incident.PublicIncidentCode, isNew, reopened, countBefore, incident.ObservationCount, priorityBefore,
            incident.PriorityScore, CpsCalculator.BandLabel(incident.PriorityBand), incident.Status.ToString(), overrode,
            assessment.Recurrence.PriorCount > 0 ? RecurrenceService.ToDto(assessment.Recurrence) : null,
            assessment.Result.Reasons, message);
    }

    // ------------------------------------------------------------------ helpers
    private async Task<CivicIncident> CreateIncidentAsync(
        ObservationDraft draft, Observation observation, IssueCategory category, DateTime now, CancellationToken ct)
    {
        var zones = await db.Zones.AsNoTracking().ToListAsync(ct);
        var point = observation.Location;
        var incident = new CivicIncident
        {
            PublicIncidentCode = "", Category = category, Latitude = draft.Latitude, Longitude = draft.Longitude,
            Location = point, LocationLabel = draft.LocationLabel, PrimaryImageUrl = observation.ImageUrl, SeverityScore = observation.SeverityScore,
            Status = IncidentStatus.Reported, FirstObservedAt = now, LastObservedAt = now, ObservationCount = 1,
            ZoneId = zones.FirstOrDefault(z => z.Boundary.Contains(point))?.Id, CreatedAt = now, UpdatedAt = now
        };
        db.IncidentObservations.Add(new IncidentObservation
        { IncidentId = incident.Id, ObservationId = observation.Id, SameIncidentScore = 1, MergeReason = "NewIncident", CreatedAt = now });

        lifecycle.AddEvent(incident.Id, "ObservationSubmitted", "Observation submitted", observation.UserId);
        lifecycle.AddEvent(incident.Id, "AiClassified",
            $"AI classified {CategoryRules.Label(draft.DetectedCategory).ToLowerInvariant()} ({draft.AiConfidence:P0})", null,
            JsonSerializer.Serialize(new { draft.DetectedCategory, draft.AiConfidence, draft.AiReason }));
        if (category != draft.DetectedCategory)
            lifecycle.AddEvent(incident.Id, "CitizenCategoryOverride",
                $"Citizen changed category from {CategoryRules.Label(draft.DetectedCategory)} to {CategoryRules.Label(category)}", observation.UserId);

        // Public codes are sequential. Two concurrent creations can pick the same number; the unique index rejects one, which retries with the next.
        for (var attempt = 0; ; attempt++)
        {
            incident.PublicIncidentCode = await NextCodeAsync(attempt, ct);
            db.Incidents.Add(incident);
            try { await db.SaveChangesAsync(ct); break; }
            catch (DbUpdateException) when (attempt < 4) { db.Entry(incident).State = EntityState.Detached; }
        }
        lifecycle.AddEvent(incident.Id, "IncidentCreated", $"Incident {incident.PublicIncidentCode} created", null, null, null, IncidentStatus.Reported);
        await db.SaveChangesAsync(ct);
        return incident;
    }

    private async Task<string> NextCodeAsync(int attempt, CancellationToken ct)
    {
        var n = 1001 + await db.Incidents.CountAsync(ct) + attempt;
        while (await db.Incidents.AnyAsync(i => i.PublicIncidentCode == $"CL-{n}", ct)) n++;
        return $"CL-{n}";
    }

    private async Task<ObservationDraft> LoadDraftAsync(Guid userId, Guid draftId, CancellationToken ct)
    {
        var draft = await db.ObservationDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct) ?? throw new NotFoundException("Analysis not found. Please analyze the photo again.");
        if (draft.UserId != userId) throw new ForbiddenException("This analysis belongs to another user.");
        if (draft.Consumed) throw new ConflictException("This observation has already been submitted.");
        if (lifecycle.Now - draft.CreatedAt > DraftLifetime) throw new ConflictException("This analysis has expired. Please analyze the photo again.");
        return draft;
    }

    private async Task<IReadOnlyList<IncidentMatchDto>> MatchesForAsync(ObservationDraft d, IssueCategory category, CancellationToken ct)
    {
        var matches = await matching.FindMatchesAsync(d.Latitude, d.Longitude, category, d.ImageSignature, d.ImageHash, lifecycle.Now, ct);
        return matches.Select(m => new IncidentMatchDto(
            m.Incident.Id, m.Incident.PublicIncidentCode, m.Incident.Category.ToString(), CategoryRules.Label(m.Incident.Category),
            (int)Math.Round(m.Score.DistanceMeters), m.Incident.FirstObservedAt, m.Incident.LastObservedAt, m.Incident.ObservationCount,
            m.Incident.ConfirmationCount, (int)Math.Round(m.Score.Total * 100),
            m.Score.Verdict == MatchVerdict.VeryLikelySame ? "VeryLikelySame" : "PossibleMatch", m.Score.Reason, m.IsResolved,
            m.Incident.PrimaryImageUrl, (int)Math.Round(m.Incident.PriorityScore), m.Incident.Status.ToString())).ToList();
    }

    private void ValidateLocation(double lat, double lon, double? devLat, double? devLon)
    {
        if (!GeoMath.IsValidCoordinate(lat, lon))
            throw new BadRequestException("The location is invalid. Please allow location access or drop a pin on the map.", "invalid_location");
        var fromCentre = _o.ServiceAreaRadiusKm <= 0 ? 0 : GeoMath.DistanceMeters(lat, lon, _o.ServiceAreaCenterLat, _o.ServiceAreaCenterLon) / 1000;
        if (_o.ServiceAreaRadiusKm > 0 && fromCentre > _o.ServiceAreaRadiusKm)
            throw new BadRequestException("This location is outside the CivicLens service area.", "outside_service_area");
        if (devLat is not null && devLon is not null && GeoMath.IsValidCoordinate(devLat.Value, devLon.Value)
            && GeoMath.DistanceMeters(lat, lon, devLat.Value, devLon.Value) > _o.MaxDeviceDistanceMeters)
            throw new BadRequestException("The selected location is far from where your device is. Please move the pin to the actual issue location.", "location_mismatch");
    }

    private async Task<string?> ResolveLocationLabelAsync(double lat, double lon, string? supplied, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(supplied)) return supplied.Trim()[..Math.Min(120, supplied.Trim().Length)];
        var street = await geocoder.ReverseAsync(lat, lon, ct); // null when disabled/offline
        var ctx = await context.AssessAsync(IssueCategory.Other, lat, lon, ct);
        var nearest = ctx.NearbyPois.OrderBy(p => p.DistanceMeters).FirstOrDefault();
        if (street is not null)
            return nearest is not null && nearest.DistanceMeters <= 120 ? $"{street} · near {nearest.Name}" : street;
        if (nearest is not null) return $"Near {nearest.Name}";
        var zones = await db.Zones.AsNoTracking().ToListAsync(ct);
        var z = zones.FirstOrDefault(z => z.Boundary.Contains(GeoMath.ToPoint(lat, lon)));
        return z is null ? null : $"{z.Name}";
    }

    public static string SeverityLabel(int s) => s >= 75 ? "High" : s >= 50 ? "Medium" : "Low";

    public static ContextDto ToContextDto(ContextRiskResult c) => new(
        (int)Math.Round(c.LocationRisk), ContextRiskCalculator.RiskLabel(c.LocationRisk), (int)Math.Round(c.Exposure), c.Reasons,
        c.NearbyPois.Select(p => new NearbyPoiDto(p.Name, p.Type.ToString(), (int)Math.Round(p.DistanceMeters))).ToList());
}
