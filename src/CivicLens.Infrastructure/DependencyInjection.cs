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
        if (string.Equals(config["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            // Zero-dependency demo mode (no Docker/PostgreSQL needed). Data is lost on restart; PostgreSQL + PostGIS is the real target.
            s.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("civiclens-demo"));
        }
        else
        {
            var cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
            s.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs, npgsql => npgsql.UseNetTopologySuite()));
        }
        s.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        s.AddSingleton(TimeProvider.System);
        s.Configure<JwtOptions>(config.GetSection("Jwt"));
        s.Configure<StorageOptions>(config.GetSection("Storage"));
        s.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        s.AddSingleton<ITokenService, JwtTokenService>();
        s.AddSingleton<IImageStorage, LocalImageStorage>();

        // AI providers are replaceable. DemoAIProvider is deterministic and works fully offline.
        s.Configure<AiOptions>(config.GetSection("AI"));
        s.AddSingleton<DemoClassificationService>();
        s.AddSingleton<DemoResolutionVerificationService>();
        s.AddSingleton<IImageSimilarityService, DemoImageSimilarityService>();
        if (string.Equals(config["AI:Provider"], "Claude", StringComparison.OrdinalIgnoreCase))
        {
            // RealAIProvider: Claude vision, automatically falling back to the demo provider on any failure.
            s.AddSingleton<ClaudeVisionClient>();
            s.AddSingleton<ClaudeClassificationService>();
            s.AddSingleton<ClaudeVerificationService>();
            s.AddSingleton<IIssueClassificationService, FallbackClassificationService>();
            s.AddSingleton<IResolutionVerificationService, FallbackVerificationService>();
        }
        else
        {
            s.AddSingleton<IIssueClassificationService>(sp => sp.GetRequiredService<DemoClassificationService>());
            s.AddSingleton<IResolutionVerificationService>(sp => sp.GetRequiredService<DemoResolutionVerificationService>());
        }
        return s;
    }
}
