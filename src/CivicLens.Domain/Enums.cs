namespace CivicLens.Domain;

public enum UserRole { Citizen = 0, Authority = 1, FieldWorker = 2 }

public enum IssueCategory
{
    Pothole = 0, GarbageAccumulation = 1, WaterLeakage = 2, SewageOverflow = 3, BrokenStreetlight = 4,
    RoadDamage = 5, DamagedFootpath = 6, FallenTree = 7, IllegalDumping = 8, DamagedPublicInfrastructure = 9,
    Other = 10,
    /// <summary>Classifier output for an after-photo in which no issue is visible. Never stored on an incident.</summary>
    NoIssue = 99
}

/// <summary>Operational lifecycle of an incident (see IncidentStateMachine).</summary>
public enum IncidentStatus
{
    Detected = 0, Reported = 1, Validated = 2, Assigned = 3, Accepted = 4, InProgress = 5,
    WorkCompleted = 6, AwaitingVerification = 7, VerifiedResolved = 8, VerificationFailed = 9,
    RequiresReview = 10, Reopened = 11, Rejected = 12
}

/// <summary>Independent of operational status: "work completed" never means "resolved".</summary>
public enum ResolutionStatus { None = 0, AwaitingVerification = 1, VerifiedResolved = 2, Disputed = 3 }

public enum PriorityBand { Low = 0, Moderate = 1, High = 2, VeryHigh = 3, Critical = 4 }

public enum ValidationStatus { Pending = 0, Verified = 1, Invalid = 2 }

public enum PoiType { School = 0, Hospital = 1, BusStop = 2, Junction = 3, Market = 4 }

public enum CitizenVote { Fixed = 0, StillExists = 1, Unsure = 2 }

public enum VerificationResult { Pending = 0, IssueNotDetected = 1, IssueStillPresent = 2, Inconclusive = 3 }

public enum MatchVerdict { VeryLikelySame = 0, PossibleMatch = 1, LikelyNew = 2 }

public enum RecurrenceRisk { None = 0, Low = 1, Medium = 2, High = 3 }

public enum HealthBand { Healthy = 0, NeedsAttention = 1, Critical = 2 }

/// <summary>Who is performing a state transition.</summary>
public enum TransitionActor { Citizen = 0, Authority = 1, Worker = 2, System = 3 }
