using CivicLens.Application.Abstractions;
using CivicLens.Application.Dtos;
using CivicLens.Application.Services;
using CivicLens.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CivicLens.Api.Controllers;

[ApiController, Authorize]
public sealed class FeedController(SocialService social, ICurrentUser user) : ControllerBase
{
    [HttpGet("api/feed")]
    public Task<PagedResult<FeedItemDto>> Feed([FromQuery] IncidentListQuery query, CancellationToken ct) => social.FeedAsync(query, user, ct);
    [HttpGet("api/incidents/{id:guid}/social")]
    public Task<SocialDto> Social(Guid id, [FromQuery] int page = 1, CancellationToken ct = default) => social.SocialAsync(id, user, page, ct);
    [HttpPut("api/incidents/{id:guid}/upvote"), Authorize(Roles = nameof(UserRole.Citizen))]
    public async Task<object> Vote(Guid id, UpvoteRequest request, CancellationToken ct) { await social.SetVoteAsync(id, user, request.Active, ct); return new { updated = true }; }
    [HttpPost("api/incidents/{id:guid}/comments")]
    public Task<CommentDto> Comment(Guid id, CommentRequest request, CancellationToken ct) => social.CommentAsync(id, user, request.Text, ct);
    [HttpDelete("api/incidents/{id:guid}/comments/{commentId:guid}")]
    public async Task<object> Delete(Guid id, Guid commentId, CancellationToken ct) { await social.DeleteCommentAsync(id, commentId, user, ct); return new { deleted = true }; }
}
