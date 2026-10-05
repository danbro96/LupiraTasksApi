using System.Text.Json.Nodes;
using Lupira.Identity.Marten;
using Lupira.Sync;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Dtos;
using LupiraTasksApi.Core.Dtos.Items;
using LupiraTasksApi.Core.Dtos.Sync;

namespace LupiraTasksApi.Core.Mappers;

/// <summary>Maps the <see cref="Item"/> snapshot to its response DTO, resolving assignee + attribution
/// principal ids to <see cref="PersonRef"/> via a lookup built by the calling service.</summary>
internal static class ItemMapper
{
    public static ItemDto ToResponse(this Item item, IReadOnlyDictionary<Guid, Principal> principals) => new()
    {
        Id = item.Id,
        ListId = item.ListId,
        ParentItemId = item.ParentItemId,
        Title = item.Title,
        Notes = item.Notes,
        Status = item.Status,
        StatusReason = item.StatusReason,
        Completed = item.Completed,
        CompletedAt = item.CompletedAt,
        CompletedBy = PersonRef.FromActor(item.CompletedBy, principals),
        Assignee = item.AssignedToPrincipalId is { } a ? PersonRef.From(a, principals) : null,
        DueAt = item.DueAt,
        Quantity = item.Quantity,
        Unit = item.Unit,
        Priority = item.Priority,
        Tags = item.Tags.ToList(),
        SortOrder = item.SortOrder,
        CreatedBy = PersonRef.FromActor(item.CreatedBy, principals),
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Metadata = string.IsNullOrWhiteSpace(item.Metadata) ? null : JsonNode.Parse(item.Metadata)?.AsObject(),
    };

    public static ItemGuardsDto ToGuards(this ItemState s) => new()
    {
        Name = SectionGuardDto.From(s.NameTs, s.NameCmd),
        Notes = SectionGuardDto.From(s.NotesTs, s.NotesCmd),
        Assignee = SectionGuardDto.From(s.AssigneeTs, s.AssigneeCmd),
        Due = SectionGuardDto.From(s.DueTs, s.DueCmd),
        Qty = SectionGuardDto.From(s.QtyTs, s.QtyCmd),
        Priority = SectionGuardDto.From(s.PriorityTs, s.PriorityCmd),
        Status = SectionGuardDto.From(s.StatusTs, s.StatusCmd),
        Move = SectionGuardDto.From(s.MoveTs, s.MoveCmd),
        Metadata = SectionGuardDto.From(s.MetadataTs, s.MetadataCmd),
        Tags = s.TagTs.ToDictionary(kv => kv.Key, kv => SectionGuardDto.From(kv.Value, s.TagCmd.GetValueOrDefault(kv.Key))),
    };

    /// <summary>Every principal id referenced by an item snapshot: assignee, plus Guid-shaped
    /// createdBy/completedBy actors (a <c>share:{label}</c> actor is not a principal and is skipped).</summary>
    public static IEnumerable<Guid> PrincipalIdsOf(this Item item)
    {
        if (item.AssignedToPrincipalId is { } a) yield return a;
        if (Guid.TryParse(item.CreatedBy, out var c)) yield return c;
        if (Guid.TryParse(item.CompletedBy, out var d)) yield return d;
    }
}
