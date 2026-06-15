using LupiraTasksApi.Application;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Http;

/// <summary>
/// Maps the transport-neutral <see cref="OpResult{T}"/>/<see cref="OpResult"/> to the typed
/// ASP.NET <c>Results&lt;...&gt;</c> unions the REST handlers declare (which keep the OpenAPI
/// contract frozen). Each helper's return type matches one handler-method shape;
/// <see cref="OpStatus.Forbidden"/> → 403 ProblemDetails, <see cref="OpStatus.Invalid"/> →
/// 400 ProblemDetails. A status a given shape can't represent is a programming error and
/// throws.
/// </summary>
internal static class OpResultMap
{
    public static Results<Ok<T>, UnauthorizedHttpResult> OkOnly<T>(OpResult<T> r) => r.Status switch
    {
        OpStatus.Ok => TypedResults.Ok(r.Value!),
        _ => throw Unexpected(r.Status),
    };

    public static Results<Ok<T>, ProblemHttpResult, UnauthorizedHttpResult> OkProblem<T>(OpResult<T> r) => r.Status switch
    {
        OpStatus.Ok => TypedResults.Ok(r.Value!),
        OpStatus.Invalid => Problems.BadRequest(r.Error!),
        OpStatus.Forbidden => Problems.Forbidden(r.Error!),
        _ => throw Unexpected(r.Status),
    };

    public static Results<Ok<T>, NotFound, UnauthorizedHttpResult> OkNotFound<T>(OpResult<T> r) => r.Status switch
    {
        OpStatus.Ok => TypedResults.Ok(r.Value!),
        OpStatus.NotFound => TypedResults.NotFound(),
        _ => throw Unexpected(r.Status),
    };

    public static Results<Ok<T>, NotFound, ProblemHttpResult, UnauthorizedHttpResult> OkNotFoundProblem<T>(OpResult<T> r) => r.Status switch
    {
        OpStatus.Ok => TypedResults.Ok(r.Value!),
        OpStatus.NotFound => TypedResults.NotFound(),
        OpStatus.Forbidden => Problems.Forbidden(r.Error!),
        OpStatus.Invalid => Problems.BadRequest(r.Error!),
        _ => throw Unexpected(r.Status),
    };

    public static Results<NoContent, NotFound, UnauthorizedHttpResult> NoContentNotFound(OpResult r) => r.Status switch
    {
        OpStatus.Ok => TypedResults.NoContent(),
        OpStatus.NotFound => TypedResults.NotFound(),
        _ => throw Unexpected(r.Status),
    };

    public static Results<NoContent, NotFound, ProblemHttpResult, UnauthorizedHttpResult> NoContentNotFoundProblem(OpResult r) => r.Status switch
    {
        OpStatus.Ok => TypedResults.NoContent(),
        OpStatus.NotFound => TypedResults.NotFound(),
        OpStatus.Forbidden => Problems.Forbidden(r.Error!),
        OpStatus.Invalid => Problems.BadRequest(r.Error!),
        _ => throw Unexpected(r.Status),
    };

    private static InvalidOperationException Unexpected(OpStatus status) =>
        new($"OpStatus '{status}' cannot be represented by this result shape.");
}
