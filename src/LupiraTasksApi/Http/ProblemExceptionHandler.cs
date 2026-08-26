using Microsoft.AspNetCore.Diagnostics;

namespace LupiraTasksApi.Http;

/// <summary>
/// Catches the faults the result mapping doesn't — genuinely unhandled ones — logs them once and returns
/// the same ProblemDetails shape as the rest of the surface, so a client has a single error-parser path.
/// The trace id rides on the <c>traceId</c> extension that AddProblemDetails stamps.
/// </summary>
internal sealed class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        log.LogError(exception, "Unhandled request exception on {Method} {Path}", context.Request.Method, context.Request.Path);

        await Results.Problem(
            title: "Internal server error",
            statusCode: StatusCodes.Status500InternalServerError,
            type: "https://httpstatuses.com/500").ExecuteAsync(context);
        return true;
    }
}
