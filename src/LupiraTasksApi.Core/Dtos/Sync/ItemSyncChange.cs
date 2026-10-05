using LupiraTasksApi.Core.Dtos.Items;

namespace LupiraTasksApi.Core.Dtos.Sync;

/// <summary>A changed item: its current state plus the per-field guards.</summary>
public sealed class ItemSyncChange
{
    public required ItemDto Item { get; set; }

    public required ItemGuardsDto Guards { get; set; }
}
