using LupiraTasksApi.Models.Items;
using LupiraTasksApi.Models.Lists;

namespace LupiraTasksApi.Models.Sync;

/// <summary>
/// A full re-derive of a list and its live items, for the offline delta-pull. v1 is
/// "simplest-correct": the whole list + all non-deleted items are returned regardless of
/// the <c>since</c> cursor, and <see cref="NextCursor"/> is the max item version seen
/// (the client plumbs the cursor through but the server ignores it for now).
/// </summary>
public sealed class SyncResponse
{
    public required ListResponse List { get; set; }
    public required IReadOnlyList<ItemResponse> Items { get; set; }

    /// <summary>Opaque cursor to pass as <c>?since=</c> on the next pull.</summary>
    public required long NextCursor { get; set; }
}
