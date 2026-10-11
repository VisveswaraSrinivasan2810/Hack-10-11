using CivicLens.Domain.Scoring;
using NetTopologySuite.Geometries;

namespace CivicLens.Domain.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public int TrustScore { get; set; } = TrustScoreCalculator.BaseScore;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A single citizen sighting. Many observations may belong to one CivicIncident.</summary>
public class Observation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ImageUrl { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public string ImageSignature { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public Point Location { get; set; } = null!;
    public string? Description { get; set; }
    public IssueCategory DetectedCategory { get; set; }
    public double AiConfidence { get; set; }
    public IssueCategory CitizenSelectedCategory { get; set; }
    public int SeverityScore { get; set; }
    public string? AiReason { get; set; }
    public bool SuspectedDuplicateImage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ValidationStatus ValidationStatus { get; set; } = ValidationStatus.Pending;
}

/// <summary>Result of "Analyze Issue": staged image + AI output awaiting the citizen's decision.</summary>
public class ObservationDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ImageUrl { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public string ImageSignature { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Description { get; set; }
    public string? LocationLabel { get; set; }
    public IssueCategory DetectedCategory { get; set; }
    public double AiConfidence { get; set; }
    public int SeverityScore { get; set; }
    public string? AiReason { get; set; }
    public bool SuspectedDuplicateImage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Consumed { get; set; }
}

public class CivicIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PublicIncidentCode { get; set; } = "";
    public IssueCategory Category { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public Point Location { get; set; } = null!;
    public string? LocationLabel { get; set; }
    public string? PrimaryImageUrl { get; set; }
    public int SeverityScore { get; set; }
    public double PriorityScore { get; set; }
    public PriorityBand PriorityBand { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Reported;
    public ResolutionStatus ResolutionStatus { get; set; } = ResolutionStatus.None;
    public DateTime FirstObservedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastObservedAt { get; set; } = DateTime.UtcNow;
    public int ObservationCount { get; set; }
    public int ConfirmationCount { get; set; }
    public int RecurrencePriorCount { get; set; }
    public Guid? AssignedWorkerId { get; set; }
    public Guid? ZoneId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int ReopenCount { get; set; }
    /// <summary>True once an authority manually overrode severity; automatic max-merging then stops.</summary>
    public bool SeverityOverridden { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive => Status is not (IncidentStatus.VerifiedResolved or IncidentStatus.Rejected);
}

public class IncidentObservation
{
    public Guid IncidentId { get; set; }
    public Guid ObservationId { get; set; }
    public double SameIncidentScore { get; set; }
    public string MergeReason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class IncidentConfirmation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PriorityScoreBreakdown
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public double Severity { get; set; }
    public double CitizenEvidence { get; set; }
    public double LocationRisk { get; set; }
    public double IncidentAge { get; set; }
    public double Exposure { get; set; }
    public double Recurrence { get; set; }
    public double FinalScore { get; set; }
    public PriorityBand Band { get; set; }
    /// <summary>JSON array of human-readable reasons ("Why Critical?").</summary>
    public string ReasonsJson { get; set; } = "[]";
    public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;
}

public class PointOfInterest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public PoiType Type { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public Point Location { get; set; } = null!;
    public double RiskWeight { get; set; }
}

public class IncidentAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Guid WorkerId { get; set; }
    public Guid AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ResolutionEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Guid WorkerId { get; set; }
    public string? BeforeImageUrl { get; set; }
    public string AfterImageUrl { get; set; } = "";
    public string AfterImageHash { get; set; } = "";
    public string? WorkerNotes { get; set; }
    public double AiConfidence { get; set; }
    public VerificationResult VerificationResult { get; set; }
    public string? AiSummary { get; set; }
    public double ResolutionConfidence { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}

public class CitizenVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Guid ResolutionEvidenceId { get; set; }
    public Guid UserId { get; set; }
    public CitizenVote Vote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Audit trail. Every meaningful event (status, score change, override) is stored here.</summary>
public class IncidentHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public string EventType { get; set; } = "";
    public string? Message { get; set; }
    public IncidentStatus? OldStatus { get; set; }
    public IncidentStatus? NewStatus { get; set; }
    public Guid? ActorId { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PotentialCorrelation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceIncidentId { get; set; }
    public Guid TargetIncidentId { get; set; }
    public string CorrelationType { get; set; } = "";
    public double Confidence { get; set; }
    public double DistanceMeters { get; set; }
    public string Explanation { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Zone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Polygon Boundary { get; set; } = null!;
    public double CenterLatitude { get; set; }
    public double CenterLongitude { get; set; }
    public double CivicHealthScore { get; set; } = 100;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? IncidentId { get; set; }
    public string EventType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
