using CivicLens.Api.Http;
using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Application.Demo;
using CivicLens.Application.Dtos;
using CivicLens.Application.Services;
using CivicLens.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CivicLens.Api.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected Guid UserId(ICurrentUser u) => u.Id ?? throw new UnauthorizedAppException("Please sign in.");

    protected static async Task<ImageUpload> ReadImageAsync(IFormFile? file, string field = "image")
    {
        if (file is null || file.Length == 0) throw new BadRequestException($"Please attach a photo ({field}).", "image_required");
        if (file.Length > ImageTools.MaxBytes) throw new BadRequestException("The image is too large. Please upload a photo under 15 MB.", "image_too_large");
        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms);
        return new ImageUpload(ms.ToArray(), file.FileName, file.ContentType);
    }
}

// ====================================================================== Auth
[Route("api/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(AuthService auth) : ApiControllerBase
{
    [HttpPost("register"), AllowAnonymous]
    public async Task<AuthResponse> Register(RegisterRequest request, CancellationToken ct) => await auth.RegisterAsync(request, ct);

    [HttpPost("login"), AllowAnonymous]
    public async Task<AuthResponse> Login(LoginRequest request, CancellationToken ct) => await auth.LoginAsync(request, ct);
}

// ====================================================================== Observations (citizen)
public sealed class AnalyzeForm
{
    public IFormFile? Image { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Description { get; set; }
    public double? DeviceLatitude { get; set; }
    public double? DeviceLongitude { get; set; }
    public string? LocationLabel { get; set; }
}

[Route("api/observations")]
[Authorize(Roles = nameof(UserRole.Citizen))]
public sealed class ObservationsController(ObservationService observations, ICurrentUser user) : ApiControllerBase
{
    /// <summary>SEE → SNAP: upload a photo + location; returns AI classification, context and ranked duplicate matches.</summary>
    [HttpPost("analyze")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [EnableRateLimiting("upload")]
    public async Task<AnalysisResponse> Analyze([FromForm] AnalyzeForm form, CancellationToken ct)
    {
        var image = await ReadImageAsync(form.Image);
        return await observations.AnalyzeAsync(UserId(user),
            new AnalyzeRequest(image, form.Latitude, form.Longitude, form.Description,
                form is { DeviceLatitude: 0, DeviceLongitude: 0 } ? null : form.DeviceLatitude, form is { DeviceLatitude: 0, DeviceLongitude: 0 } ? null : form.DeviceLongitude, form.LocationLabel), ct);
    }

    /// <summary>SUBMIT: create a new incident or attach to an existing one (human decision is always final).</summary>
    [HttpPost]
    public async Task<SubmitObservationResult> Submit(SubmitObservationRequest request, CancellationToken ct) =>
        await observations.SubmitAsync(UserId(user), request, ct);

    /// <summary>Re-rank duplicate matches after the citizen edits the AI category.</summary>
    [HttpGet("drafts/{draftId:guid}/matches")]
    public async Task<IReadOnlyList<IncidentMatchDto>> Rematch(Guid draftId, [FromQuery] string category, CancellationToken ct)
    {
        if (!Enum.TryParse<IssueCategory>(category, true, out var cat) || cat == IssueCategory.NoIssue)
            throw new BadRequestException("Unknown issue category.");
        return await observations.RematchAsync(UserId(user), draftId, cat, ct);
    }
}

// ====================================================================== Incidents
[Route("api/incidents")]
[Authorize]
public sealed class IncidentsController(IncidentService incidents, ResolutionService resolution, ICurrentUser user) : ApiControllerBase
{
    [HttpGet("nearby")]
    public async Task<IReadOnlyList<IncidentSummaryDto>> Nearby([FromQuery] double lat, [FromQuery] double lon, [FromQuery] double radius = 1000, CancellationToken ct = default) =>
        await incidents.NearbyAsync(lat, lon, radius, ct);

    [HttpGet]
    public async Task<PagedResult<IncidentSummaryDto>> List([FromQuery] IncidentListQuery query, CancellationToken ct) =>
        await incidents.ListAsync(query, user, ct);

    [HttpGet("{id:guid}")]
    public async Task<IncidentDetailDto> Get(Guid id, CancellationToken ct) => await incidents.GetDetailAsync(id, user, ct);

    /// <summary>Observations merged into this incident, with their same-incident scores (citizen identities are never exposed).</summary>
    [HttpGet("{id:guid}/matches")]
    public async Task<IReadOnlyList<ObservationPublicDto>> Matches(Guid id, CancellationToken ct) => await incidents.MatchesAsync(id, user, ct);

    [HttpPost("{id:guid}/confirm"), Authorize(Roles = nameof(UserRole.Citizen))]
    public async Task<IncidentSummaryDto> Confirm(Guid id, CancellationToken ct) => await incidents.ConfirmAsync(id, user, ct);

    [HttpPost("{id:guid}/assign"), Authorize(Roles = nameof(UserRole.Authority))]
    public async Task<IncidentSummaryDto> Assign(Guid id, AssignRequest request, CancellationToken ct) => await incidents.AssignAsync(id, request, user, ct);

    [HttpPost("{id:guid}/status"), Authorize(Roles = $"{nameof(UserRole.Authority)},{nameof(UserRole.FieldWorker)}")]
    public async Task<IncidentSummaryDto> Status(Guid id, StatusChangeRequest request, CancellationToken ct) => await incidents.ChangeStatusAsync(id, request, user, ct);

    [HttpPost("{id:guid}/override"), Authorize(Roles = nameof(UserRole.Authority))]
    public async Task<IncidentSummaryDto> Override(Guid id, OverrideRequest request, CancellationToken ct) => await incidents.OverrideAsync(id, request, user, ct);

    /// <summary>Field worker uploads the after-photo; the system verifies it against the original evidence.</summary>
    [HttpPost("{id:guid}/resolution")]
    [Authorize(Roles = nameof(UserRole.FieldWorker))]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [EnableRateLimiting("upload")]
    public async Task<ResolutionDto> SubmitResolution(Guid id, [FromForm] ResolutionForm form, CancellationToken ct) =>
        await resolution.SubmitAsync(id, user, await ReadImageAsync(form.AfterImage, "afterImage"), form.Notes, form.CompletedAt, ct);

    /// <summary>Citizen resolution check: Fixed / StillExists / Unsure.</summary>
    [HttpPost("{id:guid}/verify"), Authorize(Roles = nameof(UserRole.Citizen))]
    public async Task<ResolutionDto> Verify(Guid id, VoteRequest request, CancellationToken ct) => await resolution.VoteAsync(id, user, request, ct);
}

public sealed class ResolutionForm
{
    public IFormFile? AfterImage { get; set; }
    public string? Notes { get; set; }
    public DateTime? CompletedAt { get; set; }
}

// ====================================================================== Dashboard (authority)
[Route("api/dashboard")]
[Authorize(Roles = nameof(UserRole.Authority))]
public sealed class DashboardController(DashboardService dashboard, IncidentService incidents) : ApiControllerBase
{
    [HttpGet("summary")] public async Task<DashboardSummaryDto> Summary(CancellationToken ct) => await dashboard.SummaryAsync(ct);
    [HttpGet("hotspots")] public async Task<IReadOnlyList<HotspotDto>> Hotspots(CancellationToken ct) => await dashboard.HotspotsAsync(ct);
    [HttpGet("recurring")] public async Task<IReadOnlyList<RecurringLocationDto>> Recurring(CancellationToken ct) => await dashboard.RecurringAsync(ct);
    [HttpGet("disputed")] public async Task<IReadOnlyList<ResolutionQueueItemDto>> Disputed(CancellationToken ct) => await dashboard.DisputedAsync(ct);
    [HttpGet("verification-queue")] public async Task<IReadOnlyList<ResolutionQueueItemDto>> VerificationQueue(CancellationToken ct) => await dashboard.VerificationQueueAsync(ct);
    [HttpGet("analytics")] public async Task<AnalyticsDto> Analytics(CancellationToken ct) => await dashboard.AnalyticsAsync(ct);
    [HttpGet("correlations")] public async Task<IReadOnlyList<CorrelationDto>> Correlations(CancellationToken ct) => await incidents.CorrelationsForAsync(null, ct);
}

// ====================================================================== Shared geo data
[Route("api")]
[Authorize]
public sealed class GeoController(DashboardService dashboard, IncidentService incidents) : ApiControllerBase
{
    [HttpGet("zones")] public async Task<IReadOnlyList<ZoneDto>> Zones(CancellationToken ct) => await dashboard.ZonesAsync(ct);
    [HttpGet("zones/{id:guid}/health")] public async Task<ZoneHealthDto> ZoneHealth(Guid id, CancellationToken ct) => await dashboard.ZoneHealthAsync(id, ct);
    [HttpGet("pois")] public async Task<IReadOnlyList<PoiDto>> Pois(CancellationToken ct) => await dashboard.PoisAsync(ct);

    /// <summary>Infrastructure Memory: what has happened around a location over time.</summary>
    [HttpGet("memory")]
    public async Task<IReadOnlyList<MemoryItemDto>> Memory([FromQuery] double lat, [FromQuery] double lon, [FromQuery] double radius = 75, CancellationToken ct = default) =>
        await incidents.MemoryAsync(lat, lon, radius, null, ct);

    /// <summary>Headline city health for the citizen home screen.</summary>
    [HttpGet("city/health")]
    public async Task<object> CityHealth(CancellationToken ct)
    {
        var zones = await dashboard.ZonesAsync(ct);
        var score = zones.Count == 0 ? 100 : Math.Round(zones.Average(z => z.HealthScore), 1);
        return new { score, label = Domain.Scoring.HealthScoreCalculator.Label(Domain.Scoring.HealthScoreCalculator.BandFor(score)), zones };
    }
}

// ====================================================================== Users
[Route("api/users")]
[Authorize]
public sealed class UsersController(UserService users, IncidentService incidents, ICurrentUser user) : ApiControllerBase
{
    [HttpGet("me")] public async Task<UserDto> Me(CancellationToken ct) => await users.MeAsync(UserId(user), ct);

    [HttpGet("me/contributions"), Authorize(Roles = nameof(UserRole.Citizen))]
    public async Task<IReadOnlyList<ContributionItemDto>> Contributions(CancellationToken ct) => await users.ContributionsAsync(UserId(user), ct);

    [HttpGet("me/trust"), Authorize(Roles = nameof(UserRole.Citizen))]
    public async Task<TrustDto> Trust(CancellationToken ct) => await users.TrustAsync(UserId(user), ct);

    [HttpGet("me/notifications")]
    public async Task<IReadOnlyList<NotificationDto>> Notifications(CancellationToken ct) => await users.NotificationsAsync(UserId(user), ct);

    [HttpPost("me/notifications/read")]
    public async Task<object> MarkRead(CancellationToken ct) { await users.MarkNotificationsReadAsync(UserId(user), ct); return new { marked = true }; }

    [HttpGet("workers"), Authorize(Roles = nameof(UserRole.Authority))]
    public async Task<IReadOnlyList<WorkerDto>> Workers(CancellationToken ct) => await incidents.WorkersAsync(ct);
}

// ====================================================================== Demo mode
[Route("api/demo")]
public sealed class DemoController(DemoService demo, IConfiguration config) : ApiControllerBase
{
    private bool Enabled => config.GetValue<bool>("DEMO_MODE");

    /// <summary>Public: tells the UI whether demo mode is on and lists documented demo accounts (development only).</summary>
    [HttpGet("info"), AllowAnonymous]
    public object Info() => !Enabled
        ? new { demoMode = false }
        : new
        {
            demoMode = true,
            city = new
            {
                name = DemoCity.Name,
                centerLatitude = DemoCity.CenterLat, centerLongitude = DemoCity.CenterLon,
                reportLatitude = DemoCity.SchoolRoadLat, reportLongitude = DemoCity.SchoolRoadLon,
                reportLocationLabel = "Abids Demo School Road, Hyderabad",
                mapBounds = new { minLatitude = DemoCity.MapMinLat, maxLatitude = DemoCity.MapMaxLat,
                    minLongitude = DemoCity.MapMinLon, maxLongitude = DemoCity.MapMaxLon },
                fictionalLandmarks = true
            },
            accounts = new[]
            {
                new { role = "Citizen", email = DemoCity.CitizenEmail, password = DemoCity.DemoPassword },
                new { role = "Citizen (second)", email = DemoCity.CitizenTwoEmail, password = DemoCity.DemoPassword },
                new { role = "Authority", email = DemoCity.AuthorityEmail, password = DemoCity.DemoPassword },
                new { role = "FieldWorker", email = DemoCity.WorkerEmail, password = DemoCity.DemoPassword },
            }
        };

    [HttpGet("scenarios"), Authorize(Roles = nameof(UserRole.Authority))]
    public IReadOnlyList<DemoScenarioDto> Scenarios() { EnsureEnabled(); return demo.List(); }

    [HttpPost("scenarios/{id:int}/run"), Authorize(Roles = nameof(UserRole.Authority))]
    public async Task<DemoScenarioResult> Run(int id, CancellationToken ct) { EnsureEnabled(); return await demo.RunAsync(id, ct); }

    [HttpPost("reset"), Authorize(Roles = nameof(UserRole.Authority))]
    public async Task<object> Reset(CancellationToken ct) { EnsureEnabled(); await demo.ResetAsync(ct); return new { reset = true }; }

    private void EnsureEnabled() { if (!Enabled) throw new NotFoundException("Demo mode is not enabled."); }
}

// ====================================================================== Admin (authority)
[Route("api/admin")]
[Authorize(Roles = nameof(UserRole.Authority))]
public sealed class AdminController(AdminService admin, ICurrentUser user) : ApiControllerBase
{
    [HttpPost("pois")] public async Task<PoiDto> CreatePoi(PoiCreateRequest request, CancellationToken ct) => await admin.CreatePoiAsync(request, ct);
    [HttpDelete("pois/{id:guid}")] public async Task<object> DeletePoi(Guid id, CancellationToken ct) { await admin.DeletePoiAsync(id, ct); return new { deleted = true }; }

    /// <summary>Import schools, hospitals, markets, bus stops and junctions around a point from OpenStreetMap.</summary>
    [HttpPost("pois/import")] public async Task<object> ImportPois(PoiImportRequest request, CancellationToken ct) => await admin.ImportPoisAsync(request, ct);

    [HttpPost("zones")] public async Task<ZoneDto> CreateZone(ZoneCreateRequest request, CancellationToken ct) => await admin.CreateZoneAsync(request, ct);
    [HttpDelete("zones/{id:guid}")] public async Task<object> DeleteZone(Guid id, CancellationToken ct) { await admin.DeleteZoneAsync(id, ct); return new { deleted = true }; }

    [HttpGet("staff")] public async Task<IReadOnlyList<AdminUserDto>> Staff(CancellationToken ct) => await admin.ListStaffAsync(ct);
    [HttpPost("users")] public async Task<AdminUserDto> CreateUser(AdminUserRequest request, CancellationToken ct) => await admin.CreateUserAsync(request, ct);
    [HttpPost("users/{id:guid}/active")]
    public async Task<AdminUserDto> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) => await admin.SetActiveAsync(id, request.Active, user, ct);
}

[Route("api/account")]
[Authorize]
public sealed class AccountController(UserService users, IPasswordHasher hasher, ICurrentUser user) : ApiControllerBase
{
    [HttpPost("password")]
    public async Task<object> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await users.ChangePasswordAsync(UserId(user), request.CurrentPassword, request.NewPassword, hasher, ct);
        return new { changed = true };
    }
}
