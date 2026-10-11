using System.Data;
using CivicLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CivicLens.Api.Http;

/// <summary>Keep multi-step workflow writes atomic in PostgreSQL. Demo InMemory has no transactions.</summary>
public sealed class MutationTransactionFilter(AppDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!db.Database.IsRelational() || HttpMethods.IsGet(context.HttpContext.Request.Method)) { await next(); return; }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, context.HttpContext.RequestAborted);
        var result = await next();
        if (result.Exception is null && !result.Canceled)
            await transaction.CommitAsync(context.HttpContext.RequestAborted);
    }
}
