using CivicLens.Domain;

namespace CivicLens.Application.Dtos;

// ---------- Auth ----------
public sealed record RegisterRequest(string Name, string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record UserDto(Guid Id, string Name, string Email, string Role, int TrustScore);
public sealed record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);

// ---------- Observation flow ----------
public sealed record CategoryOptionDto(string Category, string Label, int ConfidencePercent);

public sealed record NearbyPoiDto(string Name, string Type, int DistanceMeters);

public sealed record ContextDto(
    int LocationRisk, string LocationRiskLabel, int Exposure, IReadOnlyList<string> Reasons, IReadOnlyList<NearbyPoiDto> NearbyPois);

public sealed record IncidentMatchDto(
    Guid IncidentId, string Code, string Category, string CategoryLabel, int DistanceMeters, DateTime FirstObservedAt,
    DateTime LastObservedAt, int ObservationCount, int ConfirmationCount, int SameIncidentPercent, string Verdict,
    string Reason, bool IsResolved, string? ImageUrl, int PriorityScore, string Status);

public sealed record AnalysisResponse(
    Guid DraftId, string ImageUrl, string DetectedCategory, string DetectedCategoryLabel, int ConfidencePercent,
    bool IsLowConfidence, IReadOnlyList<CategoryOptionDto> Alternatives, int Severity, string SeverityLabel, string Reason,
    string Provider, string? LocationLabel, ContextDto Context, IReadOnlyList<IncidentMatchDto> Matches,
    RecurrenceDto? Recurrence, IReadOnlyList<string> Warnings);

public sealed record SubmitObservationRequest(
    Guid DraftId, string? SelectedCategory, string? Description, string Action, Guid? IncidentId);

public sealed record SubmitObservationResult(
    Guid IncidentId, string Code, bool IsNewIncident, bool ReopenedIncident, int ObservationCountBefore, int ObservationCountAfter,
    double PriorityBefore, double PriorityAfter, string PriorityBand, string Status, bool OverrodeLikelyMatch,
    RecurrenceDto? Recurrence, IReadOnlyList<string> Reasons, string Message);

// ---------- Incidents ----------
public sealed record IncidentSummaryDto(
    Guid Id, string Code, string Category, string CategoryLabel, double Latitude, double Longitude, string? LocationLabel,
    double PriorityScore, string PriorityBand, string Status, string ResolutionStatus, int ObservationCount, int ConfirmationCount,
    DateTime FirstObservedAt, DateTime LastUpdated, double AgeHours, string? AssigneeName, Guid? AssignedWorkerId,
    bool IsRecurring, int RecurrencePriorCount, string? PrimaryImageUrl, string? ZoneName, int? DistanceMeters);

public sealed record PriorityBreakdownDto(
    double Severity, double CitizenEvidence, double LocationRisk, double IncidentAge, double Exposure, double Recurrence,
    double FinalScore, string Band, IReadOnlyList<string> Reasons, double? PreviousScore, DateTime CalculatedAt);

public sealed record ObservationPublicDto(
    Guid Id, string ImageUrl, DateTime CreatedAt, string Category, int ConfidencePercent, int SameIncidentPercent,
    string MergeReason, bool IsMine, bool SuspectedDuplicateImage);

public sealed record TimelineItemDto(DateTime At, string EventType, string Message, string? OldStatus, string? NewStatus);

public sealed record PriorIncidentDto(Guid Id, string Code, string Category, string CategoryLabel, DateTime OccurredAt, int DistanceMeters);

public sealed record RecurrenceDto(
    bool IsRecurring, string Risk, int PriorCount, int RelatedTotal, int WindowDays, string Summary, string? Banner,
    IReadOnlyList<PriorIncidentDto> Priors);

public sealed record MemoryItemDto(Guid IncidentId, string Code, string Category, string CategoryLabel, DateTime At, string Status, bool IsCurrent);

public sealed record CorrelationDto(
    Guid Id, Guid SourceIncidentId, string SourceCode, string SourceCategoryLabel, DateTime SourceDate,
    Guid TargetIncidentId, string TargetCode, string TargetCategoryLabel, DateTime TargetDate,
    int DistanceMeters, string ConfidenceLabel, double Confidence, string Explanation);

public sealed record CitizenVerificationDto(int Fixed, int StillExists, int Unsure, int Total, int? ConfidencePercent);

public sealed record ResolutionDto(
    Guid EvidenceId, string? BeforeImageUrl, string AfterImageUrl, string? WorkerNotes, string VerificationResult,
    int AiConfidencePercent, string? BeforeSummary, string? AfterSummary, string? AiReason,
    CitizenVerificationDto Citizens, int ResolutionConfidencePercent, string Outcome, string OutcomeLabel,
    string? NeutralNote, DateTime SubmittedAt);

public sealed record IncidentDetailDto(
    IncidentSummaryDto Summary, int Severity, PriorityBreakdownDto Priority, ContextDto Context,
    IReadOnlyList<ObservationPublicDto> Observations, IReadOnlyList<TimelineItemDto> Timeline, RecurrenceDto Recurrence,
    IReadOnlyList<MemoryItemDto> Memory, IReadOnlyList<CorrelationDto> Correlations, ResolutionDto? Resolution,
    IReadOnlyList<string> AllowedNextStatuses, bool CanConfirm, bool CanVerifyResolution, string? MyVote, bool IsContributor,
    string StatusBanner);

public sealed record IncidentListQuery
{
    public string? Status { get; init; }
    public string? Category { get; init; }
    public string? Band { get; init; }
    public double? MinPriority { get; init; }
    public bool? ActiveOnly { get; init; }
    public bool? Recurring { get; init; }
    public bool? Disputed { get; init; }
    public bool? AssignedToMe { get; init; }
    public Guid? AssignedWorkerId { get; init; }
    public string? Search { get; init; }
    public string? Sort { get; init; } // priority (default) | newest | oldest
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record AssignRequest(Guid WorkerId, string? Note);
public sealed record StatusChangeRequest(string Status, string? Reason);
public sealed record OverrideRequest(string? Category, int? Severity, string Reason);
public sealed record VoteRequest(string Vote, double? Latitude, double? Longitude);

public sealed record WorkerDto(Guid Id, string Name, int ActiveAssignments);

// ---------- Dashboard ----------
public sealed record DashboardSummaryDto(
    int ActiveIncidents, int CriticalIncidents, int HighPriorityIncidents, int ResolvedToday, int RecurringIssues,
    int DisputedResolutions, int AwaitingVerification, int EmergingHotspots, double AverageResolutionHours,
    double CivicHealthScore, string CivicHealthLabel, int TotalObservations, int TotalIncidents, double ObservationsPerIncident);

public sealed record HotspotDto(
    int Rank, string Name, double Latitude, double Longitude, int RadiusMeters, int IncidentCount, int CriticalCount,
    int RecurringCount, IReadOnlyDictionary<string, int> CategoryCounts, IReadOnlyDictionary<string, int> Last14Days,
    int Last14DaysTotal, int Previous14DaysTotal, double TrendPercent, string Priority, double PriorityScore, bool IsEmerging,
    string Title, IReadOnlyList<Guid> IncidentIds);

public sealed record RecurringLocationDto(
    string Name, double Latitude, double Longitude, int IncidentCount, string Risk, IReadOnlyList<string> Categories,
    IReadOnlyList<MemoryItemDto> Timeline, bool HasActiveIncident);

public sealed record ZoneDto(Guid Id, string Name, double CenterLatitude, double CenterLongitude, double HealthScore, string HealthLabel,
    IReadOnlyList<LatLonDto> Boundary);

public sealed record LatLonDto(double Lat, double Lon);

public sealed record ZoneHealthDto(
    Guid ZoneId, string Name, double Score, string Label, int ActiveIncidents, int CriticalIncidents, int RecurringIncidents,
    int DisputedResolutions, double AverageResolutionHours, IReadOnlyList<string> Penalties);

public sealed record ChartPoint(string Label, double Value);

public sealed record AnalyticsDto(
    IReadOnlyList<ChartPoint> ByCategory, IReadOnlyList<ChartPoint> ByStatus, IReadOnlyList<ChartPoint> AvgResolutionHoursByCategory,
    IReadOnlyList<ChartPoint> RecurrenceTrend, IReadOnlyList<ChartPoint> ZoneHealth, IReadOnlyList<ChartPoint> CpsDistribution);

public sealed record PoiDto(Guid Id, string Name, string Type, double Latitude, double Longitude);

// ---------- Users ----------
public sealed record ContributionItemDto(
    string Kind, Guid IncidentId, string IncidentCode, string CategoryLabel, string Status, DateTime At, string? ImageUrl, string? Note);

public sealed record TrustDto(
    int Score, int VerifiedObservations, int AccurateClassifications, int UsefulConfirmations, int ResolutionChecks,
    int InvalidSubmissions, int TotalObservations, IReadOnlyList<string> Factors, string Level);

public sealed record NotificationDto(Guid Id, string EventType, string Title, string Message, bool IsRead, DateTime CreatedAt, Guid? IncidentId);

// ---------- Demo ----------
public sealed record DemoScenarioDto(int Id, string Name, string Description);
public sealed record DemoScenarioResult(int Id, string Name, Guid? IncidentId, string? IncidentCode, string Summary, IReadOnlyList<string> Highlights);

public sealed record ResolutionQueueItemDto(IncidentSummaryDto Summary, ResolutionDto? Resolution);
