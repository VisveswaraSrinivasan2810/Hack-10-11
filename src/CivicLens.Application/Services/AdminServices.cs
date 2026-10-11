using System.Collections.Concurrent;
using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Application.Dtos;
using CivicLens.Domain;
using CivicLens.Domain.Configuration;
using CivicLens.Domain.Entities;
using CivicLens.Domain.Scoring;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CivicLens.Application.Services;

public sealed record PoiCreateRequest(string Name, string Type, double Latitude, double Longitude, double? RiskWeight);
public sealed record PoiImportRequest(double Latitude, double Longitude, double? RadiusMeters);
public sealed record ZoneCreateRequest(string Name, IReadOnlyList<LatLonDto> Boundary);
public sealed record AdminUserRequest(string Name, string Email, string Password, string Role);
public sealed record SetActiveRequest(bool Active);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AdminUserDto(Guid Id, string Name, string Email, string Role, bool IsActive, DateTime CreatedAt);

/// <summary>
/// Makes contextual risk work anywhere: the first report in an area with no known places triggers a one-off OpenStreetMap import
/// (schools, hospitals, markets, bus stops, signalised junctions). Failure-tolerant; never blocks a citizen's report.
/// </summary>
public sealed class PoiCoverageService(IAppDbContext db, IPoiImporter importer, TimeProvider clock, IConfiguration config, IOptions<ScoringOptions> options)
{
    private static readonly ConcurrentDictionary<(int, int), DateTime> Attempts = new();
    private readonly ScoringOptions _o = options.Value;

    public async Task EnsureAsync(double lat, double lon, CancellationToken ct = default)
    {
        if (!_o.PoiAutoImport || config.GetValue<bool>("DEMO_MODE")) return;
        var cell = ((int)Math.Floor(lat / 0.02), (int)Math.Floor(lon / 0.02));
        var now = clock.GetUtcNow().UtcDateTime;
        if (Attempts.TryGetValue(cell, out var last) && now - last < TimeSpan.FromHours(24)) return;

        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(lat, lon, _o.PoiCoverageRadiusMeters);
        if (await db.PointsOfInterest.AnyAsync(p => p.Latitude >= minLat && p.Latitude <= maxLat && p.Longitude >= minLon && p.Longitude <= maxLon, ct)) return;

        Attempts[cell] = now;
        try { await ImportAsync(lat, lon, _o.PoiCoverageRadiusMeters, ct); }
        catch (Exception) { /* enrichment only */ }
    }

    public async Task<int> ImportAsync(double lat, double lon, double radiusMeters, CancellationToken ct = default)
    {
        var found = await importer.FindAroundAsync(lat, lon, radiusMeters, ct);
        if (found.Count == 0) return 0;
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(lat, lon, radiusMeters + 200);
        var existing = await db.PointsOfInterest.AsNoTracking()
            .Where(p => p.Latitude >= minLat && p.Latitude <= maxLat && p.Longitude >= minLon && p.Longitude <= maxLon).ToListAsync(ct);

        var added = 0;
        foreach (var p in found.Take(800))
        {
            if (existing.Any(e => e.Type == p.Type && GeoMath.DistanceMeters(e.Latitude, e.Longitude, p.Latitude, p.Longitude) < 25)) continue;
            var poi = new PointOfInterest
            {
                Name = p.Name, Type = p.Type, Latitude = p.Latitude, Longitude = p.Longitude,
                Location = GeoMath.ToPoint(p.Latitude, p.Longitude), RiskWeight = ContextRiskCalculator.DefaultWeight(p.Type)
            };
            db.PointsOfInterest.Add(poi);
            existing.Add(poi);
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }
}

/// <summary>Authority-only data management: places, zones and staff accounts.</summary>
public sealed class AdminService(IAppDbContext db, PoiCoverageService poiCoverage, IPasswordHasher hasher, TimeProvider clock)
{
    public async Task<PoiDto> CreatePoiAsync(PoiCreateRequest r, CancellationToken ct = default)
    {
        if (!Enum.TryParse<PoiType>(r.Type, true, out var type)) throw new BadRequestException("Unknown place type. Use School, Hospital, BusStop, Junction or Market.");
        if (!GeoMath.IsValidCoordinate(r.Latitude, r.Longitude)) throw new BadRequestException("Invalid coordinates.", "invalid_location");
        var weight = Math.Clamp(r.RiskWeight ?? ContextRiskCalculator.DefaultWeight(type), 0, 100);
        var poi = new PointOfInterest
        {
            Name = r.Name.Trim(), Type = type, Latitude = r.Latitude, Longitude = r.Longitude,
            Location = GeoMath.ToPoint(r.Latitude, r.Longitude), RiskWeight = weight
        };
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync(ct);
        return new PoiDto(poi.Id, poi.Name, poi.Type.ToString(), poi.Latitude, poi.Longitude);
    }

    public async Task DeletePoiAsync(Guid id, CancellationToken ct = default)
    {
        var poi = await db.PointsOfInterest.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Place not found.");
        db.PointsOfInterest.Remove(poi);
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> ImportPoisAsync(PoiImportRequest r, CancellationToken ct = default)
    {
        if (!GeoMath.IsValidCoordinate(r.Latitude, r.Longitude)) throw new BadRequestException("Invalid coordinates.", "invalid_location");
        var added = await poiCoverage.ImportAsync(r.Latitude, r.Longitude, Math.Clamp(r.RadiusMeters ?? 3000, 200, 5000), ct);
        return new { added, message = added == 0 ? "No new places were found (or the OpenStreetMap service is unreachable)." : $"{added} places imported." };
    }

    public async Task<ZoneDto> CreateZoneAsync(ZoneCreateRequest r, CancellationToken ct = default)
    {
        if (r.Boundary.Count < 3) throw new BadRequestException("A zone needs at least 3 boundary points.");
        if (r.Boundary.Any(p => !GeoMath.IsValidCoordinate(p.Lat, p.Lon))) throw new BadRequestException("Invalid boundary coordinates.", "invalid_location");
        var ring = r.Boundary.Select(p => (p.Lat, p.Lon)).ToList();
        var polygon = GeoMath.ToPolygon(ring);
        if (!polygon.IsValid) throw new BadRequestException("The zone boundary is not a valid polygon (it may cross itself).");

        var zone = new Zone
        {
            Name = r.Name.Trim(), Boundary = polygon,
            CenterLatitude = ring.Average(p => p.Lat), CenterLongitude = ring.Average(p => p.Lon)
        };
        db.Zones.Add(zone);
        // Existing incidents that were outside every zone may now belong to this one.
        foreach (var i in await db.Incidents.Where(i => i.ZoneId == null).ToListAsync(ct))
            if (polygon.Contains(i.Location)) i.ZoneId = zone.Id;
        await db.SaveChangesAsync(ct);
        return new ZoneDto(zone.Id, zone.Name, zone.CenterLatitude, zone.CenterLongitude, 100, "HEALTHY", r.Boundary);
    }

    public async Task DeleteZoneAsync(Guid id, CancellationToken ct = default)
    {
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct) ?? throw new NotFoundException("Zone not found.");
        foreach (var i in await db.Incidents.Where(i => i.ZoneId == id).ToListAsync(ct)) i.ZoneId = null;
        db.Zones.Remove(zone);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AdminUserDto> CreateUserAsync(AdminUserRequest r, CancellationToken ct = default)
    {
        if (!Enum.TryParse<UserRole>(r.Role, true, out var role)) throw new BadRequestException("Role must be Citizen, Authority or FieldWorker.");
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new ConflictException("An account with this email already exists.");
        var user = new User
        {
            Name = r.Name.Trim(), Email = email, Role = role, PasswordHash = hasher.Hash(r.Password), CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task<IReadOnlyList<AdminUserDto>> ListStaffAsync(CancellationToken ct = default) =>
        (await db.Users.AsNoTracking().Where(u => u.Role != UserRole.Citizen).OrderBy(u => u.Role).ThenBy(u => u.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<AdminUserDto> SetActiveAsync(Guid id, bool active, ICurrentUser actor, CancellationToken ct = default)
    {
        if (actor.Id == id && !active) throw new BadRequestException("You cannot deactivate your own account.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User not found.");
        user.IsActive = active;
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    private static AdminUserDto ToDto(User u) => new(u.Id, u.Name, u.Email, u.Role.ToString(), u.IsActive, u.CreatedAt);
}

public sealed class PoiCreateRequestValidator : AbstractValidator<PoiCreateRequest>
{
    public PoiCreateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Type).NotEmpty();
        RuleFor(x => x.RiskWeight).InclusiveBetween(0, 100).When(x => x.RiskWeight.HasValue);
    }
}

public sealed class ZoneCreateRequestValidator : AbstractValidator<ZoneCreateRequest>
{
    public ZoneCreateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Boundary).NotNull().Must(b => b is { Count: >= 3 and <= 500 }).WithMessage("A zone needs between 3 and 500 boundary points.");
    }
}

public sealed class AdminUserRequestValidator : AbstractValidator<AdminUserRequest>
{
    public AdminUserRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Email).NotEmpty().MaximumLength(160).EmailAddress();
        RuleFor(x => x.Password).Must(PasswordRules.IsStrong).WithMessage(PasswordRules.Message);
        RuleFor(x => x.Role).NotEmpty();
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).Must(PasswordRules.IsStrong).WithMessage(PasswordRules.Message);
    }
}

public static class PasswordRules
{
    public const string Message = "Password must be 8-100 characters and contain at least one letter and one digit.";
    public static bool IsStrong(string? p) => p is { Length: >= 8 and <= 100 } && p.Any(char.IsLetter) && p.Any(char.IsDigit);
}
