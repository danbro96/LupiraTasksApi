namespace LupiraTasksApi.Mcp;

/// <summary>A batch node resolved to the item it will create: its id, parent, title, and sort key.</summary>
public sealed record PlannedTask(Guid Id, Guid? ParentId, string Title, string SortOrder, int Level);
