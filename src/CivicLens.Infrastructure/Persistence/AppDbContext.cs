using CivicLens.Application.Abstractions;
using CivicLens.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CivicLens.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<IncidentUpvote> IncidentUpvotes => Set<IncidentUpvote>();
    public DbSet<IncidentComment> IncidentComments => Set<IncidentComment>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<ObservationDraft> ObservationDrafts => Set<ObservationDraft>();
    public DbSet<CivicIncident> Incidents => Set<CivicIncident>();
    public DbSet<IncidentObservation> IncidentObservations => Set<IncidentObservation>();
    public DbSet<IncidentConfirmation> IncidentConfirmations => Set<IncidentConfirmation>();
    public DbSet<PriorityScoreBreakdown> PriorityBreakdowns => Set<PriorityScoreBreakdown>();
    public DbSet<PointOfInterest> PointsOfInterest => Set<PointOfInterest>();
    public DbSet<IncidentAssignment> Assignments => Set<IncidentAssignment>();
    public DbSet<ResolutionEvidence> ResolutionEvidence => Set<ResolutionEvidence>();
    public DbSet<CitizenVerification> CitizenVerifications => Set<CitizenVerification>();
    public DbSet<IncidentHistory> History => Set<IncidentHistory>();
    public DbSet<PotentialCorrelation> Correlations => Set<PotentialCorrelation>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("postgis");
        b.Entity<IncidentUpvote>(e =>
        {
            e.HasKey(x => new { x.IncidentId, x.UserId });
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<IncidentComment>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(1000);
            e.HasIndex(x => new { x.IncidentId, x.CreatedAt, x.Id });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Email).HasMaxLength(160);
        });

        b.Entity<Observation>(e =>
        {
            e.Property(x => x.Location).HasColumnType("geography (point)");
            e.HasIndex(x => x.Location).HasMethod("gist");
            e.HasIndex(x => x.ImageHash);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ObservationDraft>(e => e.HasIndex(x => new { x.UserId, x.CreatedAt }));

        b.Entity<CivicIncident>(e =>
        {
            e.Property(x => x.Location).HasColumnType("geography (point)");
            e.HasIndex(x => x.Location).HasMethod("gist");
            e.HasIndex(x => x.PublicIncidentCode).IsUnique();
            e.HasIndex(x => new { x.Latitude, x.Longitude });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.PriorityScore);
            e.Ignore(x => x.IsActive);
        });

        b.Entity<IncidentObservation>(e =>
        {
            e.HasKey(x => new { x.IncidentId, x.ObservationId });
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Observation>().WithMany().HasForeignKey(x => x.ObservationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IncidentConfirmation>(e =>
        {
            e.HasIndex(x => new { x.IncidentId, x.UserId }).IsUnique();
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PriorityScoreBreakdown>(e =>
        {
            e.HasIndex(x => new { x.IncidentId, x.CalculatedAt });
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PointOfInterest>(e =>
        {
            e.Property(x => x.Location).HasColumnType("geography (point)");
            e.HasIndex(x => x.Location).HasMethod("gist");
        });

        b.Entity<IncidentAssignment>(e =>
        {
            e.HasIndex(x => x.WorkerId);
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ResolutionEvidence>(e =>
        {
            e.HasIndex(x => x.IncidentId);
            e.Property(x => x.WorkerNotes).HasMaxLength(1000);
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CitizenVerification>(e =>
        {
            e.HasIndex(x => new { x.ResolutionEvidenceId, x.UserId }).IsUnique();
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IncidentHistory>(e =>
        {
            e.HasIndex(x => new { x.IncidentId, x.CreatedAt });
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PotentialCorrelation>(e =>
        {
            e.HasIndex(x => new { x.SourceIncidentId, x.TargetIncidentId }).IsUnique();
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.SourceIncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CivicIncident>().WithMany().HasForeignKey(x => x.TargetIncidentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Zone>(e => e.Property(x => x.Boundary).HasColumnType("geography (polygon)"));

        b.Entity<Notification>(e => e.HasIndex(x => new { x.UserId, x.CreatedAt }));

        // Npgsql 'timestamptz' only accepts UTC values: normalise every DateTime on the way in/out.
        var utc = new ValueConverter<DateTime, DateTime>(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcN = new ValueConverter<DateTime?, DateTime?>(
            v => v == null ? v : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
            v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (p.ClrType == typeof(DateTime)) p.SetValueConverter(utc);
            else if (p.ClrType == typeof(DateTime?)) p.SetValueConverter(utcN);
        }
    }
}
