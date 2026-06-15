namespace LupiraTasksApi.Domain;

/// <summary>Drives client UI affordances (e.g. shopping lists surface quantity/unit).</summary>
public enum ListKind
{
    Todo,
    Shopping,
}

/// <summary>A member's authority on a list. Ordered: Owner &gt; Editor &gt; Viewer.</summary>
public enum ListRole
{
    Owner,
    Editor,
    Viewer,
}

/// <summary>What a public share link permits.</summary>
public enum ShareAccess
{
    Read,
    ReadWrite,
}
