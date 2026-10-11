using System.Security.Claims;
using System.Text.Json;
using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CivicLens.Api.Http;

// ------------------------------------------------------------------ envelope
public sealed record ApiError(string Code, string Message, IReadOnlyList<string>? Details = null);

public sealed record ApiResponse<T>(bool Success, T? Data, ApiError? Error, string CorrelationId);

public static class Envelope
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string CorrelationId(HttpContext ctx) =>
        ctx.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var v) && v is string s ? s : ctx.TraceIdentifier;

    public static ApiResponse<object?> Fail(HttpContext ctx, string code, string message, IReadOnlyList<string>? details = null) =>
        new(false, null, new ApiError(code, message, details), CorrelationId(ctx));

    public static async Task WriteAsync(HttpContext ctx, int status, ApiResponse<object?> body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}

/// <summary>Wraps every successful controller result in { success, data, error, correlationId }.</summary>
public sealed class EnvelopeResultFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var cid = Envelope.CorrelationId(context.HttpContext);
        switch (context.Result)
        {
            case ObjectResult { Value: not null } o when o.Value.GetType().IsGenericType && o.Value.GetType().GetGenericTypeDefinition() == typeof(ApiResponse<>):
                return;
            case ObjectResult o when (o.StatusCode ?? 200) < 400:
                context.Result = new ObjectResult(new ApiResponse<object?>(true, o.Value, null, cid)) { StatusCode = o.StatusCode ?? 200 };
                break;
            case EmptyResult or StatusCodeResult { StatusCode: < 400 }:
                context.Result = new ObjectResult(new ApiResponse<object?>(true, null, null, cid)) { StatusCode = 200 };
                break;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}

/// <summary>Runs FluentValidation for every action argument that has a registered validator.</summary>
public sealed class ValidationFilter(IServiceProvider sp) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var arg in context.ActionArguments.Values.Where(a => a is not null))
        {
            var validatorType = typeof(IValidator<>).MakeGenericType(arg!.GetType());
            if (sp.GetService(validatorType) is not IValidator validator) continue;
            var result = await validator.ValidateAsync(new ValidationContext<object>(arg));
            if (!result.IsValid)
                throw new BadRequestWithDetails(result.Errors[0].ErrorMessage, "validation_failed", result.Errors.Select(e => e.ErrorMessage).Distinct().ToList());
        }
        await next();
    }
}

// ------------------------------------------------------------------ correlation + errors
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string ItemKey = "CorrelationId";
    public const string Header = "X-Correlation-Id";

    public async Task Invoke(HttpContext ctx)
    {
        var id = ctx.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) id = Guid.NewGuid().ToString("N");
        ctx.Items[ItemKey] = id;
        ctx.Response.Headers[Header] = id;
        using (Serilog.Context.LogContext.PushProperty("CorrelationId", id)) await next(ctx);
    }
}

public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task Invoke(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (AppException ex)
        {
            await Envelope.WriteAsync(ctx, ex.StatusCode, Envelope.Fail(ctx, ex.Code, ex.Message, ex is BadRequestWithDetails d ? d.Details : null));
        }
        catch (BadHttpRequestException ex)
        {
            await Envelope.WriteAsync(ctx, ex.StatusCode, Envelope.Fail(ctx, "bad_request", "The request could not be read. Check the upload size and format."));
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Unique-constraint race (double tap, two devices): the first request won; tell the second one plainly.
            await Envelope.WriteAsync(ctx, 409, Envelope.Fail(ctx, "conflict", "That was already recorded. Please refresh and try again."));
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            await Envelope.WriteAsync(ctx, 409, Envelope.Fail(ctx, "conflict", "Someone else changed this at the same time. Please refresh and try again."));
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* client went away */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            await Envelope.WriteAsync(ctx, 500, Envelope.Fail(ctx, "server_error",
                $"Something went wrong on our side. Please try again. (reference {Envelope.CorrelationId(ctx)})"));
        }
    }
}

/// <summary>Validation failure carrying every message (kept in the API project so Application stays HTTP-agnostic).</summary>
public class BadRequestWithDetails(string message, string code, IReadOnlyList<string> details) : AppException(400, code, message)
{
    public IReadOnlyList<string> Details { get; } = details;
}

// ------------------------------------------------------------------ current user
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? Id => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public UserRole? Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(ClaimTypes.Role), out var r) ? r : null;
}
