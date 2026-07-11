using System.Text.Json.Nodes;
using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Dtos.Items;

/// <summary>An item's current snapshot. Clients nest by <see cref="ParentItemId"/>.</summary>
public sealed class ItemResponse
{
    public required Guid Id { get; set; }
    public required int Version { get; set; }
    public required Guid ListId { get; set; }
    public Guid? ParentItemId { get; set; }
    public required string Title { get; set; }
    public string? Notes { get; set; }
    /// <summary>The lifecycle status; <see cref="Completed"/> is the derived <c>Status == Done</c> convenience.</summary>
    public required ItemStatus Status { get; set; }
    public string? StatusReason { get; set; }
    public required bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Who completed it; <c>null</c> if open, or a share-link/unresolved actor.</summary>
    public PersonRef? CompletedBy { get; set; }
    /// <summary>The assignee; <c>null</c> if unassigned.</summary>
    public PersonRef? Assignee { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public required int Priority { get; set; }
    public required IReadOnlyList<Guid> Tags { get; set; }
    public required string SortOrder { get; set; }
    /// <summary>Who created it; <c>null</c> for a share-link/unresolved actor.</summary>
    public PersonRef? CreatedBy { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Free-form JSON for agent/server bookkeeping. Server-side only — never in VTODO or the public share DTO.</summary>
    public JsonNode? Metadata { get; set; }
}

/// <summary>Envelope for a list's items.</summary>
public sealed class ItemCollectionResponse
{
    public required IReadOnlyList<ItemResponse> Items { get; set; }
}
