namespace LupiraTasksApi.Core.Application.Results;

/// <summary>A no-content operation outcome (e.g. delete / leave).</summary>
public readonly record struct OpResult(OpStatus Status, string? Error)
{
    public bool IsOk => Status == OpStatus.Ok;

    public static OpResult Ok() => new(OpStatus.Ok, null);
    public static OpResult NotFound() => new(OpStatus.NotFound, null);
    public static OpResult Forbidden(string error) => new(OpStatus.Forbidden, error);
    public static OpResult Invalid(string error) => new(OpStatus.Invalid, error);
    public static OpResult Conflict(string error) => new(OpStatus.Conflict, error);
}
