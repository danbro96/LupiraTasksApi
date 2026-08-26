namespace LupiraTasksApi.Core.Domain;

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
