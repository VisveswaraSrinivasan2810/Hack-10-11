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
        var cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
        s.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs, npgsql => npgsql.UseNetTopologySuite()));
        s.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        s.AddSingleton(TimeProvider.System);
        s.Configure<JwtOptions>(config.GetSection("Jwt"));
        s.Configure<StorageOptions>(config.GetSection("Storage"));
        s.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        s.AddSingleton<ITokenService, JwtTokenService>();
        s.AddSingleton<IImageStorage, LocalImageStorage>();

        // AI providers are replaceable. DemoAIProvider is deterministic and works fully offline.
        s.AddSingleton<IIssueClassificationService, DemoClassificationService>();
        s.AddSingleton<IImageSimilarityService, DemoImageSimilarityService>();
        s.AddSingleton<IResolutionVerificationService, DemoResolutionVerificationService>();
        return s;
    }
}
