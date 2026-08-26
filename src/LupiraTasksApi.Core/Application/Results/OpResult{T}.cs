namespace LupiraTasksApi.Core.Application.Results;

/// <summary>A value-returning operation outcome.</summary>
public readonly record struct OpResult<T>(OpStatus Status, T? Value, string? Error)
{
    public bool IsOk => Status == OpStatus.Ok;

    public static OpResult<T> Ok(T value) => new(OpStatus.Ok, value, null);
    public static OpResult<T> NotFound() => new(OpStatus.NotFound, default, null);
    public static OpResult<T> Forbidden(string error) => new(OpStatus.Forbidden, default, error);
    public static OpResult<T> Invalid(string error) => new(OpStatus.Invalid, default, error);
    public static OpResult<T> Conflict(string error) => new(OpStatus.Conflict, default, error);
}
