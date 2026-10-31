using System.Diagnostics;
using HashRelay.Contracts;

namespace HashRelay.Server.Auth;

/// <summary>Problem reporting in the contract's <c>{code,message,traceId}</c> envelope.</summary>
public static class ApiErrors
{
    public static string TraceId(HttpContext ctx) => Activity.Current?.Id ?? ctx.TraceIdentifier;

    public static ApiError Build(HttpContext ctx, string code, string message, IReadOnlyDictionary<string, string[]>? details = null)
        => new(code, message, TraceId(ctx), details);

    public static Task WriteAsync(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(Build(ctx, code, message));
    }

    public static IResult Validation(HttpContext ctx, string message, IReadOnlyDictionary<string, string[]>? details = null)
        => Results.Json(Build(ctx, ErrorCodes.Validation, message, details), statusCode: StatusCodes.Status400BadRequest);

    public static IResult Validation(HttpContext ctx, string field, string message)
        => Validation(ctx, message, new Dictionary<string, string[]> { [field] = [message] });

    public static IResult NotFound(HttpContext ctx, string message)
        => Results.Json(Build(ctx, ErrorCodes.NotFound, message), statusCode: StatusCodes.Status404NotFound);

    public static IResult Forbidden(HttpContext ctx, string message)
        => Results.Json(Build(ctx, ErrorCodes.Forbidden, message), statusCode: StatusCodes.Status403Forbidden);

    public static IResult Conflict(HttpContext ctx, string message)
        => Results.Json(Build(ctx, ErrorCodes.Conflict, message), statusCode: StatusCodes.Status409Conflict);
}

/// <summary>Thrown by services for caller errors; translated to the envelope by the API layer.</summary>
public sealed class ApiException(int status, string code, string message, IReadOnlyDictionary<string, string[]>? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]>? Details { get; } = details;

    public static ApiException Validation(string message, string? field = null)
        => new(400, ErrorCodes.Validation, message, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });
    public static ApiException NotFound(string message) => new(404, ErrorCodes.NotFound, message);
    public static ApiException Forbidden(string message) => new(403, ErrorCodes.Forbidden, message);
    public static ApiException Conflict(string message) => new(409, ErrorCodes.Conflict, message);
}
