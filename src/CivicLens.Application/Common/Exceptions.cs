namespace CivicLens.Application.Common;

/// <summary>Human-readable failure that maps to an HTTP status in the API layer.</summary>
public class AppException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class NotFoundException(string message) : AppException(404, "not_found", message);
public sealed class ForbiddenException(string message) : AppException(403, "forbidden", message);
public sealed class ConflictException(string message) : AppException(409, "conflict", message);
public sealed class BadRequestException(string message, string code = "bad_request") : AppException(400, code, message);
public sealed class InvalidTransitionException(string message) : AppException(409, "invalid_transition", message);
public sealed class RateLimitException(string message) : AppException(429, "rate_limited", message);
public sealed class UnauthorizedAppException(string message) : AppException(401, "unauthorized", message);
