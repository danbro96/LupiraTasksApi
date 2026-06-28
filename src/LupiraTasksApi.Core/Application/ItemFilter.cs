using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Application;

/// <summary>Optional in-memory filters for an item listing (all null = no filtering).</summary>
public readonly record struct ItemFilter(bool? Completed, Guid? TagId, Guid? ParentItemId, string? AssignedTo, ItemStatus? Status = null);
