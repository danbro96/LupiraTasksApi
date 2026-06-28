namespace LupiraTasksApi.Domain;

/// <summary>Drives client UI affordances (e.g. shopping lists surface quantity/unit), and distinguishes
/// agent/system-owned lists from a user's own (<see cref="Agent"/>) in queries and UI. A pure label set at
/// creation (carried by <c>ListCreated</c>); ownership/membership still govern access.</summary>
public enum ListKind
{
    Todo,
    Shopping,

    /// <summary>An agent/system-owned list (the assistant's own backlog, or operator ops work). Functionally a
    /// list like any other — ownership is via OwnerEmail + membership; this only marks its provenance.</summary>
    Agent,
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

/// <summary>
/// An item's lifecycle position — one state machine, not two flags. <see cref="Open"/>, <see cref="InProgress"/>,
/// <see cref="Blocked"/>, and <see cref="Waiting"/> are open; <see cref="Done"/> and <see cref="Cancelled"/> are
/// closed. <c>Completed</c> is derived (<c>Status == Done</c>). The value is LWW-guarded by one
/// (OccurredAt, CommandId) guard shared by every lifecycle event, so done-ness and the open sub-state can never
/// disagree. Lets the assistant answer "what's blocked / waiting on me?".
/// </summary>
public enum ItemStatus
{
    Open,
    InProgress,
    Blocked,
    Waiting,
    Done,
    Cancelled,
}
