namespace LupiraTasksApi.Core.Domain;

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
