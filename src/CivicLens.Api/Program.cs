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
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

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
if (string.IsNullOrWhiteSpace(jwt.Key) || Encoding.UTF8.GetByteCount(jwt.Key) < 32)
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
    await next();
});

var uploadsPath = Path.GetFullPath(builder.Configuration["Storage:Path"] ?? "uploads");
Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(uploadsPath), RequestPath = "/uploads" });

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
if (builder.Configuration.GetValue("Auth:Disabled", false)) { app.UseDemoNoAuth(); app.Logger.LogWarning("Auth:Disabled=true — the API is OPEN (no login needed). Set Auth:Disabled=false to enforce authorization."); }
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// ---------------------------------------------------------------- database + demo data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();

    if (builder.Configuration.GetValue<bool>("DEMO_MODE"))
    {
        var seeded = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedIfEmptyAsync();
        app.Logger.LogInformation("DEMO_MODE is ON ({Result}). Demo accounts use the documented development password.", seeded ? "seeded demo city" : "demo data already present");
    }
}

app.Run();

public partial class Program;
