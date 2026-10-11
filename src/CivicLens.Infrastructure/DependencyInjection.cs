using CivicLens.Application.Abstractions;
using CivicLens.Infrastructure.Ai;
using CivicLens.Infrastructure.Persistence;
using CivicLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicLens.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCivicLensInfrastructure(this IServiceCollection s, IConfiguration config)
    {
        var demoMode = config.GetValue<bool>("DEMO_MODE");

        if (string.Equals(config["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            // Zero-dependency demo mode (no Docker/PostgreSQL needed). Data is lost on restart; PostgreSQL + PostGIS is the real target.
            s.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("civiclens-demo"));
        }
        else
        {
            var cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
            s.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs, npgsql =>
            {
                npgsql.UseNetTopologySuite();
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            }));
        }
        s.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        s.AddSingleton(TimeProvider.System);
        s.Configure<JwtOptions>(config.GetSection("Jwt"));
        s.Configure<StorageOptions>(config.GetSection("Storage"));
        s.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        s.AddSingleton<ITokenService, JwtTokenService>();
        s.AddSingleton<IImageStorage, LocalImageStorage>();

        // Image pipeline: decode → fix phone rotation → strip all metadata → cap resolution (storage + AI copies).
        s.Configure<ImageOptions>(config.GetSection("Images"));
        s.PostConfigure<ImageOptions>(o => { if (config["Images:AllowUndecodable"] is null) o.AllowUndecodable = demoMode; });
        s.AddSingleton<IImageProcessor, SkiaImageProcessor>();
        s.AddSingleton<IImageSimilarityService, ImageSimilarityService>();

        // AI providers are replaceable.
        //   Claude configured (or AI:Provider=Auto with ANTHROPIC_API_KEY) → real vision on real photos.
        //   DEMO_MODE and no key → DemoAIProvider (deterministic, offline, driven by file-name hints).
        //   Production without a key → "unavailable": citizens pick the category themselves and resolution needs human verification.
        s.Configure<AiOptions>(config.GetSection("AI"));
        s.AddSingleton<DemoClassificationService>();
        s.AddSingleton<DemoResolutionVerificationService>();
        var ai = config.GetSection("AI").Get<AiOptions>() ?? new AiOptions();
        if (ai.UseClaude())
        {
            s.AddSingleton<ClaudeVisionClient>();
            s.AddSingleton<ClaudeClassificationService>();
            s.AddSingleton<ClaudeVerificationService>();
            s.AddSingleton<IIssueClassificationService, FallbackClassificationService>();
            s.AddSingleton<IResolutionVerificationService, FallbackVerificationService>();
        }
        else if (demoMode)
        {
            s.AddSingleton<IIssueClassificationService>(sp => sp.GetRequiredService<DemoClassificationService>());
            s.AddSingleton<IResolutionVerificationService>(sp => sp.GetRequiredService<DemoResolutionVerificationService>());
        }
        else
        {
            s.AddSingleton<IIssueClassificationService, UnavailableClassificationService>();
            s.AddSingleton<IResolutionVerificationService, UnavailableVerificationService>();
        }

        // Optional OpenStreetMap-backed enrichment. Both are failure-tolerant and switched off in DEMO_MODE (offline demos).
        s.Configure<GeocodingOptions>(config.GetSection("Geocoding"));
        s.PostConfigure<GeocodingOptions>(o => { if (config["Geocoding:Enabled"] is null) o.Enabled = !demoMode; });
        s.Configure<PoiOptions>(config.GetSection("Poi"));
        s.AddHttpClient<IGeocoder, NominatimGeocoder>();
        s.AddHttpClient<IPoiImporter, OverpassPoiImporter>();
        return s;
    }
}
