using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Application.Dtos;
using CivicLens.Domain;
using CivicLens.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CivicLens.Application.Services;

public sealed class SocialService(IAppDbContext db, IncidentService incidents, TimeProvider clock)
{
    private async Task CheckAccess(Guid id, ICurrentUser user, CancellationToken ct)
    {
        if (user.Id is null) throw new UnauthorizedAppException("Please sign in.");
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Incident not found.");
        if (user.Role == UserRole.FieldWorker && incident.AssignedWorkerId != user.Id)
            throw new ForbiddenException("This incident is not assigned to you.");
    }

    public async Task<PagedResult<FeedItemDto>> FeedAsync(IncidentListQuery query, ICurrentUser user, CancellationToken ct)
    {
        var page = await incidents.ListAsync(query, user, ct);
        var ids = page.Items.Select(i => i.Id).ToList();
        var votes = await db.IncidentUpvotes.AsNoTracking().Where(v => ids.Contains(v.IncidentId))
            .GroupBy(v => v.IncidentId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
        var mine = await db.IncidentUpvotes.AsNoTracking().Where(v => ids.Contains(v.IncidentId) && v.UserId == user.Id).Select(v => v.IncidentId).ToListAsync(ct);
        var comments = await db.IncidentComments.AsNoTracking().Where(c => ids.Contains(c.IncidentId))
            .GroupBy(c => c.IncidentId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
        var voteMap = votes.ToDictionary(x => x.Id, x => x.Count);
        var commentMap = comments.ToDictionary(x => x.Id, x => x.Count);
        return new(page.Items.Select(i => new FeedItemDto(i, voteMap.GetValueOrDefault(i.Id), mine.Contains(i.Id),
            commentMap.GetValueOrDefault(i.Id), i.AssigneeName is null ? [] : [i.AssigneeName])).ToList(), page.Total, page.Page, page.PageSize);
    }

    public async Task<SocialDto> SocialAsync(Guid id, ICurrentUser user, int page, CancellationToken ct)
    {
        await CheckAccess(id, user, ct);
        page = Math.Clamp(page, 1, 1_000_000);
        var query = db.IncidentComments.AsNoTracking().Where(c => c.IncidentId == id);
        var total = await query.CountAsync(ct);
        var rows = await (from c in query join u in db.Users on c.UserId equals u.Id
                          orderby c.CreatedAt descending, c.Id
                          select new { c.Id, c.Text, c.CreatedAt, c.UserId, u.Role }).Skip((page - 1) * 30).Take(30).ToListAsync(ct);
        var items = rows.Select(c => new CommentDto(c.Id, c.Role == UserRole.FieldWorker ? "Field worker" : c.Role.ToString(), c.Text, c.CreatedAt,
            c.UserId == user.Id || user.Role == UserRole.Authority)).ToList();
        return new(await db.IncidentUpvotes.CountAsync(v => v.IncidentId == id, ct),
            await db.IncidentUpvotes.AnyAsync(v => v.IncidentId == id && v.UserId == user.Id, ct), total, new(items, total, page, 30));
    }

    public async Task SetVoteAsync(Guid id, ICurrentUser user, bool active, CancellationToken ct)
    {
        await CheckAccess(id, user, ct);
        if (user.Role != UserRole.Citizen) throw new ForbiddenException("Only citizens may upvote incidents.");
        var vote = await db.IncidentUpvotes.FirstOrDefaultAsync(v => v.IncidentId == id && v.UserId == user.Id, ct);
        if (active && vote is null) db.IncidentUpvotes.Add(new IncidentUpvote { IncidentId = id, UserId = user.Id!.Value, CreatedAt = clock.GetUtcNow().UtcDateTime });
        if (!active && vote is not null) db.IncidentUpvotes.Remove(vote);
        await db.SaveChangesAsync(ct);
    }

    public async Task<CommentDto> CommentAsync(Guid id, ICurrentUser user, string text, CancellationToken ct)
    {
        await CheckAccess(id, user, ct);
        text = text?.Trim() ?? "";
        if (text.Length is < 1 or > 1000) throw new BadRequestException("Comments must be 1–1000 characters.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (await db.IncidentComments.CountAsync(c => c.UserId == user.Id && c.CreatedAt > now.AddMinutes(-1), ct) >= 10)
            throw new RateLimitException("Please wait before adding more comments.");
        var comment = new IncidentComment { IncidentId = id, UserId = user.Id!.Value, Text = text, CreatedAt = now };
        db.IncidentComments.Add(comment);
        await db.SaveChangesAsync(ct);
        return new(comment.Id, user.Role == UserRole.FieldWorker ? "Field worker" : user.Role.ToString()!, text, now, true);
    }

    public async Task DeleteCommentAsync(Guid id, Guid commentId, ICurrentUser user, CancellationToken ct)
    {
        await CheckAccess(id, user, ct);
        var comment = await db.IncidentComments.FirstOrDefaultAsync(c => c.Id == commentId && c.IncidentId == id, ct) ?? throw new NotFoundException("Comment not found.");
        if (comment.UserId != user.Id && user.Role != UserRole.Authority) throw new ForbiddenException("You cannot delete this comment.");
        db.IncidentComments.Remove(comment);
        await db.SaveChangesAsync(ct);
    }
}
