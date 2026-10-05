using Lupira.Sync.Marten;

namespace LupiraTasksApi.Core.Application.Sync;

/// <summary>Configuration for the sync feeds (bound from the <c>Sync</c> section).</summary>
public sealed class SyncFeedOptions
{
    public const string SectionName = "Sync";

    /// <summary>How old an event must be before a feed reports it, so a slower concurrent commit with a lower
    /// sequence is never skipped.</summary>
    public TimeSpan SettleLag { get; set; } = EventLogExtensions.DefaultSettleLag;
}
