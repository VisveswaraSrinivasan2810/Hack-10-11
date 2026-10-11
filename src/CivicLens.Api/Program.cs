using System.Text;
using System.Threading.RateLimiting;
using CivicLens.Api.Http;
using CivicLens.Application;
using CivicLens.Application.Demo;
using CivicLens.Infrastructure;
using CivicLens.Infrastructure.Persistence;
using CivicLens.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var provisionAuthority = args.Contains("--provision-authority", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--provision-authority").ToArray());

if (builder.Configuration.GetValue<bool>("Auth:Disabled") &&
    (!builder.Environment.IsDevelopment() || !builder.Configuration.GetValue<bool>("DEMO_MODE")))
    throw new InvalidOperationException("Authentication bypass is allowed only in a Development demo.");
if (!builder.Environment.IsDevelopment() &&
    string.Equals(builder.Configuration["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Production requires a persistent PostgreSQL/PostGIS database.");
if (!builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Images:AllowUndecodable"))
    throw new InvalidOperationException("Production cannot accept undecodable images.");

// Local demo needs no database server, cloud account or manually supplied key.
if (builder.Configuration.GetValue<bool>("DEMO_MODE"))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("DEMO_MODE is only allowed in Development.");
    if (string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Key"]))
        builder.Configuration["Jwt:Key"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
}

builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}"));

// ---------------------------------------------------------------- services
builder.Services.AddCivicLensApplication(builder.Configuration);
builder.Services.AddCivicLensInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CivicLens.Application.Abstractions.ICurrentUser, CurrentUser>();

builder.Services.AddControllers(o =>
{
    o.Filters.Add<EnvelopeResultFilter>();
    o.Filters.Add<ValidationFilter>();
    o.Filters.Add<MutationTransactionFilter>();
}).ConfigureApiBehaviorOptions(o =>
{
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var errors = ctx.ModelState.Values.SelectMany(v => v.Errors).Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).Distinct().ToList();
        return new ObjectResult(Envelope.Fail(ctx.HttpContext, "validation_failed", errors.FirstOrDefault() ?? "The request is invalid.", errors)) { StatusCode = 400 };
    };
}).AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 20 * 1024 * 1024);

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if ((!builder.Environment.IsDevelopment() && jwt.Key.StartsWith("dev-only-", StringComparison.Ordinal))
    || string.IsNullOrWhiteSpace(jwt.Key) || Encoding.UTF8.GetByteCount(jwt.Key) < 32)
    throw new InvalidOperationException("Jwt:Key must be set (environment variable Jwt__Key) to a secret of at least 32 characters. See .env.example.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1), NameClaimType = System.Security.Claims.ClaimTypes.Name,
        RoleClaimType = System.Security.Claims.ClaimTypes.Role
    };
    o.MapInboundClaims = false;
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async ctx =>
        {
            var idText = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(idText, out var id)) { ctx.Fail("Invalid account."); return; }
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ctx.HttpContext.RequestAborted);
            if (account is null || !account.IsActive || account.SecurityStamp != ctx.Principal?.FindFirst("security_stamp")?.Value
                || account.Role.ToString() != ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value)
                ctx.Fail("This session is no longer valid. Please sign in again.");
        },
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            await Envelope.WriteAsync(ctx.HttpContext, 401, Envelope.Fail(ctx.HttpContext, "unauthorized", "Please sign in to continue."));
        },
        OnForbidden = ctx => Envelope.WriteAsync(ctx.HttpContext, 403, Envelope.Fail(ctx.HttpContext, "forbidden", "Your account is not allowed to do that."))
    };
});
builder.Services.AddAuthorization();

var perMinute = builder.Configuration.GetValue("RateLimit:PerMinute", 240);
var authPerMinute = builder.Configuration.GetValue("RateLimit:AuthPerMinute", 20);
var uploadsPerMinute = builder.Configuration.GetValue("RateLimit:UploadsPerMinute", 30);
builder.Services.AddRateLimiter(o =>
{
    static string Key(HttpContext c) => c.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "anon";
    o.RejectionStatusCode = 429;
    o.OnRejected = (ctx, _) => new ValueTask(Envelope.WriteAsync(ctx.HttpContext, 429,
        Envelope.Fail(ctx.HttpContext, "rate_limited", "Too many requests. Please slow down and try again shortly.")));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        RateLimitPartition.GetFixedWindowLimiter(Key(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("upload", c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = uploadsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(CorrelationIdMiddleware.Header)));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "CivicLens AI API", Version = "v1", Description = "From Citizen Observation to Verified Resolution" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "Paste the JWT returned by /api/auth/login" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
    o.SupportNonNullableReferenceTypes();
    if (builder.Configuration.GetValue("Auth:Disabled", false)) o.OperationFilter<DemoUserHeaderFilter>();
});

builder.Services.AddHealthChecks();

// ---------------------------------------------------------------- pipeline
var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();
app.UseSerilogRequestLogging();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/uploads"))
        ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

var uploadsPath = Path.GetFullPath(builder.Configuration["Storage:Path"] ?? "uploads");
Directory.CreateDirectory(uploadsPath);
app.UseDefaultFiles();
app.UseStaticFiles();
// Evidence is served by an authenticated controller, never by public static-file middleware.

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseRouting();
app.UseCors();
app.UseAuthentication();
if (builder.Configuration.GetValue("Auth:Disabled", false)) { app.UseDemoNoAuth(); app.Logger.LogWarning("Auth:Disabled=true — the API is OPEN (no login needed). Set Auth:Disabled=false to enforce authorization."); }
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { ready = true }) : Results.StatusCode(503));


// ---------------------------------------------------------------- database + demo data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
    {
        if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Database:AutoMigrate"))
            await db.Database.MigrateAsync();
        else if ((await db.Database.GetPendingMigrationsAsync()).Any())
            throw new InvalidOperationException("Database migrations must be applied before starting Production. See docs/API_INTEGRATION.md.");
    }
    else await db.Database.EnsureCreatedAsync();

    if (provisionAuthority)
    {
        var email = builder.Configuration["Bootstrap:Email"]?.Trim().ToLowerInvariant();
        var name = builder.Configuration["Bootstrap:Name"]?.Trim();
        var password = builder.Configuration["Bootstrap:Password"];
        if (string.IsNullOrWhiteSpace(email) || email.Length > 160 || !System.Net.Mail.MailAddress.TryCreate(email, out _)
            || string.IsNullOrWhiteSpace(name) || name.Length > 80
            || !CivicLens.Application.Services.PasswordRules.IsStrong(password))
            throw new InvalidOperationException("Provisioning requires valid Bootstrap__Email, Bootstrap__Name and Bootstrap__Password. Supply the password through secure environment settings.");
        if (await db.Users.AnyAsync(u => u.Role == CivicLens.Domain.UserRole.Authority))
            throw new InvalidOperationException("An authority already exists. Use the authenticated staff administration API.");
        var hasher = scope.ServiceProvider.GetRequiredService<CivicLens.Application.Abstractions.IPasswordHasher>();
        db.Users.Add(new CivicLens.Domain.Entities.User { Name = name, Email = email, PasswordHash = hasher.Hash(password!), Role = CivicLens.Domain.UserRole.Authority });
        await db.SaveChangesAsync();
        app.Logger.LogInformation("Initial authority provisioned. Remove Bootstrap variables before starting the server.");
        return;
    }
    if (builder.Configuration.GetValue<bool>("DEMO_MODE"))
    {
        var seeded = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedIfEmptyAsync();
        app.Logger.LogInformation("DEMO_MODE is ON ({Result}). Demo accounts use the documented development password.", seeded ? "seeded demo city" : "demo data already present");
    }
}

app.Run();

public partial class Program;
