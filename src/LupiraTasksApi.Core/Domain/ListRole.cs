namespace LupiraTasksApi.Core.Domain;

/// <summary>A member's authority on a list. Ordered: Owner &gt; Editor &gt; Viewer.</summary>
public enum ListRole
{
    Owner,
    Editor,
    Viewer,
}
