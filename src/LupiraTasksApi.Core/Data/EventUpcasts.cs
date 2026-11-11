using LupiraTasksApi.Core.Domain.Items.Events;

namespace LupiraTasksApi.Core.Data;

/// <summary>Reads persisted older event schemas as their current shape.</summary>
internal static class EventUpcasts
{
    public static ItemDueDateSetV2 DueDateSet(ItemDueDateSet old) =>
        new(old.ItemId, DueOn: null, old.DueAt, old.OccurredAt, old.CommandId);

    public static ItemVtodoPutV2 VtodoPut(ItemVtodoPut old) =>
        new(old.ItemId, old.ListId, old.Uid, old.Title, old.Notes, DueOn: null, old.DueAt, old.Status, old.CompletedAt,
            old.Tags, old.SortOrder, old.SourceVtodo, old.OccurredAt, old.CommandId, old.Priority);
}
