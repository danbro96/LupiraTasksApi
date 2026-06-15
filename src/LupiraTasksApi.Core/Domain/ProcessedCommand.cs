namespace LupiraTasksApi.Domain;

/// <summary>
/// Idempotency record: marks a command as already processed so a redelivered
/// command is a no-op rather than a duplicate write. Placeholder document — the
/// real command/aggregate model lands in a later phase.
/// </summary>
public sealed class ProcessedCommand
{
    /// <summary>Marten document identity — the originating command id.</summary>
    public Guid CommandId { get; set; }

    public Guid AggregateId { get; set; }

    public int ResultVersion { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
