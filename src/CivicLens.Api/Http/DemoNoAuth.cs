using System.Security.Claims;
using CivicLens.Application.Demo;
using CivicLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CivicLens.Api.Http;

/// <summary>
/// TEMPORARY open mode (Auth:Disabled=true). Requests without a token are treated as the seeded demo user whose role the endpoint requires
/// (citizen for reporting, authority for dashboard/assign, worker for resolution). Send header X-Demo-User: &lt;email&gt; to act as someone else
/// (e.g. citizen2@civiclens.demo, or worker@civiclens.demo on the shared authority/worker status endpoint).
/// A real Bearer token, if supplied, is still honoured. Set Auth:Disabled=false to restore normal authorization.
/// </summary>
public static class DemoNoAuth
{
    public const string Header = "X-Demo-User";

    public static void UseDemoNoAuth(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true)
            {
                var roles = ctx.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(a => a.Roles).Where(r => !string.IsNullOrWhiteSpace(r))
                    .SelectMany(r => r!.Split(',')).Select(r => r.Trim()).ToList() ?? [];

                var requested = ctx.Request.Headers[Header].FirstOrDefault();
                var email = !string.IsNullOrWhiteSpace(requested) ? requested.Trim().ToLowerInvariant()
                    : roles.Contains("Citizen") ? DemoCity.CitizenEmail
                    : roles.Contains("Authority") ? DemoCity.AuthorityEmail
                    : roles.Contains("FieldWorker") ? DemoCity.WorkerEmail
                    : DemoCity.CitizenEmail;

                var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
                if (user is not null)
                {
                    var identity = new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new Claim(ClaimTypes.Name, user.Name),
                        new Claim(ClaimTypes.Email, user.Email),
                        new Claim(ClaimTypes.Role, user.Role.ToString()),
                    }, "DemoNoAuth");
                    ctx.User = new ClaimsPrincipal(identity);
                }
            }
            await next();
        });
    }
}

/// <summary>Adds the optional X-Demo-User header to every Swagger operation while open mode is on.</summary>
public sealed class DemoUserHeaderFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = DemoNoAuth.Header, In = ParameterLocation.Header, Required = false,
            Description = "Open mode only. Act as this demo user, e.g. citizen2@civiclens.demo or worker@civiclens.demo. Leave empty to use the default user for the endpoint's role.",
            Schema = new OpenApiSchema { Type = "string" }
        });
    }
}
