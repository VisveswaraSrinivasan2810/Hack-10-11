using CivicLens.Application.Abstractions;
using CivicLens.Domain;
using CivicLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CivicLens.Api.Controllers;

[ApiController, Authorize]
public sealed class EvidenceController(AppDbContext db, ICurrentUser user, IConfiguration config) : ControllerBase
{
    [HttpGet("/uploads/{name}")]
    public async Task<IActionResult> Get(string name, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-f0-9]{32}\\.(jpg|png|webp)$")) return NotFound();
        var url = "/uploads/" + name;
        var own = await db.Observations.AnyAsync(o => o.ImageUrl == url && o.UserId == user.Id, ct);
        var linked = await (from io in db.IncidentObservations
                            join o in db.Observations on io.ObservationId equals o.Id
                            join i in db.Incidents on io.IncidentId equals i.Id
                            where o.ImageUrl == url && (user.Role != UserRole.FieldWorker || i.AssignedWorkerId == user.Id)
                            select i.Id).AnyAsync(ct);
        var resolution = await (from e in db.ResolutionEvidence join i in db.Incidents on e.IncidentId equals i.Id
                                where e.AfterImageUrl == url && (user.Role != UserRole.FieldWorker || i.AssignedWorkerId == user.Id)
                                select e.Id).AnyAsync(ct);
        if (!own && !linked && !resolution) return NotFound();
        var path = Path.Combine(Path.GetFullPath(config["Storage:Path"] ?? "uploads"), name);
        if (!System.IO.File.Exists(path)) return NotFound();
        var mime = name.EndsWith(".png") ? "image/png" : name.EndsWith(".webp") ? "image/webp" : "image/jpeg";
        return PhysicalFile(path, mime);
    }
}
