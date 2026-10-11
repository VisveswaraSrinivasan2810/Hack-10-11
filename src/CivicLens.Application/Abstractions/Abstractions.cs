using CivicLens.Domain;
using CivicLens.Domain.Entities;
using CivicLens.Domain.Scoring;
using Microsoft.EntityFrameworkCore;

namespace CivicLens.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Observation> Observations { get; }
    DbSet<ObservationDraft> ObservationDrafts { get; }
    DbSet<CivicIncident> Incidents { get; }
    DbSet<IncidentObservation> IncidentObservations { get; }
    DbSet<IncidentConfirmation> IncidentConfirmations { get; }
    DbSet<PriorityScoreBreakdown> PriorityBreakdowns { get; }
    DbSet<PointOfInterest> PointsOfInterest { get; }
    DbSet<IncidentAssignment> Assignments { get; }
    DbSet<ResolutionEvidence> ResolutionEvidence { get; }
    DbSet<CitizenVerification> CitizenVerifications { get; }
    DbSet<IncidentHistory> History { get; }
    DbSet<PotentialCorrelation> Correlations { get; }
    DbSet<Zone> Zones { get; }
    DbSet<Notification> Notifications { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public sealed record ImageUpload(byte[] Bytes, string FileName, string ContentType);

public sealed record ClassificationResult(
    IssueCategory Category, double Confidence, int Severity, IReadOnlyList<CategoryProbability> Alternatives,
    string Reason, string Provider);

public sealed record CategoryProbability(IssueCategory Category, double Probability);

public sealed record ResolutionCheckResult(
    VerificationResult Result, double Confidence, string BeforeSummary, string AfterSummary, string Reason, string Provider);

/// <summary>Image understanding: what civic problem (if any) is visible? Replaceable (Real vs Demo provider).</summary>
public interface IIssueClassificationService
{
    Task<ClassificationResult> ClassifyAsync(ImageUpload image, string? description, CancellationToken ct = default);
}

/// <summary>Visual similarity between two stored images, 0..1.</summary>
public interface IImageSimilarityService
{
    /// <summary>Compact, storable signature of an image (computed once, compared many times).</summary>
    string ComputeSignature(byte[] imageBytes);
    Task<double> CompareAsync(string signatureA, string hashA, string signatureB, string hashB, CancellationToken ct = default);
}

public interface IResolutionVerificationService
{
    Task<ResolutionCheckResult> VerifyAsync(
        IssueCategory beforeCategory, ImageUpload afterImage, string? workerNotes, CancellationToken ct = default);
}

public interface IContextRiskService
{
    Task<ContextRiskResult> AssessAsync(IssueCategory category, double lat, double lon, CancellationToken ct = default);
}

public interface IImageStorage
{
    /// <summary>Persists bytes and returns a public, non-filesystem URL such as /uploads/abc.jpg.</summary>
    Task<string> SaveAsync(byte[] bytes, string extension, CancellationToken ct = default);
}

public interface INotificationService
{
    Task NotifyAsync(Guid userId, string eventType, string title, string message, Guid? incidentId, CancellationToken ct = default);
    Task NotifyRoleAsync(UserRole role, string eventType, string title, string message, Guid? incidentId, CancellationToken ct = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface ITokenService
{
    (string Token, DateTime ExpiresAt) Create(User user);
}

public interface ICurrentUser
{
    Guid? Id { get; }
    UserRole? Role { get; }
}
