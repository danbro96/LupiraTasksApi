using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Http;

/// <summary>
/// Shared helpers for emitting RFC 7807 ProblemDetails responses. Every handler that
/// returns 4xx with a message goes through these so the wire shape is consistent —
/// <c>application/problem+json</c> with <c>{ type, title, detail, status }</c> — and the
/// clients have a single error-parser path.
/// </summary>
internal static class Problems
{
    public static ProblemHttpResult BadRequest(string detail, string? title = null) =>
        TypedResults.Problem(
            title: title ?? "Bad request",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            type: "https://httpstatuses.com/400");

    public static ProblemHttpResult Conflict(string detail, string? title = null) =>
        TypedResults.Problem(
            title: title ?? "Conflict",
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            type: "https://httpstatuses.com/409");
}
