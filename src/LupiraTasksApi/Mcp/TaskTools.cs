using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Dtos.Lists;
using LupiraTasksApi.Dtos.Relations;
using LupiraTasksApi.Dtos.Shares;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LupiraTasksApi.Mcp;

/// <summary>
/// The agent's MCP surface. These tools are intent-shaped (coarser than the REST CRUD) and
/// call the SAME <see cref="ListService"/>/<see cref="ItemService"/> as the REST handlers, so
/// there is no second source of truth. The agent never deals with GUIDv7 ids, fractional-index
/// sort keys, <c>*Provided</c> flags, or idempotency keys — the tools mint those server-side.
/// Identity comes from the bearer principal on the MCP transport (<see cref="CurrentUser"/>),
/// so every call is scoped to that member's own + shared lists via the services' membership checks.
/// </summary>
[McpServerToolType]
public sealed class TaskTools
{
    private readonly CurrentUser _user;
    private readonly ListService _lists;
    private readonly ItemService _items;
    private readonly RelationService _relations;
    private readonly ShareService _shares;

    public TaskTools(CurrentUser user, ListService lists, ItemService items, RelationService relations, ShareService shares)
    {
        _user = user;
        _lists = lists;
        _items = items;
        _relations = relations;
        _shares = shares;
    }

    /// <summary>A list as the agent sees it — trimmed, with the caller's own role.</summary>
    public sealed record ListSummary(Guid Id, string Name, ListKind Kind, ListRole? Role, bool IsArchived, bool SimplePriority);

    /// <summary>A task as the agent sees it — trimmed, with its owning list named.</summary>
    public sealed record TaskSummary(
        Guid Id, Guid ListId, string ListName, string Title, ItemStatus Status, bool Completed, DateTimeOffset? DueAt, string? AssignedTo, int Priority);

    /// <summary>A cross-API link as the agent sees it — the edge tuple needed to read or unlink it.</summary>
    public sealed record RelationSummary(Guid Id, string ToKind, string ToRef, string RelationType, JsonNode? Metadata);

    [McpServerTool(Name = "list_my_lists")]
    [Description("List the to-do / shopping lists the current user is a member of, with their role on each.")]
    public async Task<IReadOnlyList<ListSummary>> ListMyLists(
        [Description("Include archived lists as well (default false).")] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var lists = (await _lists.ListAsync(caller, archived: false, ct)).Value!.Lists.ToList();
        if (includeArchived)
            lists.AddRange((await _lists.ListAsync(caller, archived: true, ct)).Value!.Lists);
        return lists.Select(l => Summarize(caller, l)).ToList();
    }

    [McpServerTool(Name = "create_list")]
    [Description("Create a new list owned by the current user.")]
    public async Task<ListSummary> CreateList(
        [Description("Display name of the list.")] string name,
        [Description("List kind: Todo, Shopping, or Agent (default Todo). Use Agent for the assistant's own backlog or operator/ops lists.")] ListKind kind = ListKind.Todo,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var request = new CreateListRequest { Id = Guid.CreateVersion7(), Name = name, Kind = kind };
        var created = Require(await _lists.CreateAsync(caller, Guid.CreateVersion7(), request, ct));
        return Summarize(caller, created);
    }

    [McpServerTool(Name = "find_tasks")]
    [Description("Find tasks across the user's lists. Optionally scope to one list, or filter by a title substring, " +
        "completion state, assignee, or lifecycle status (e.g. Blocked/Waiting to see what's stuck).")]
    public async Task<IReadOnlyList<TaskSummary>> FindTasks(
        [Description("Restrict to a single list id (optional; searches all the user's lists when omitted).")] Guid? listId = null,
        [Description("Case-insensitive substring to match in the task title (optional).")] string? query = null,
        [Description("Filter by completion state (optional).")] bool? completed = null,
        [Description("Filter by assignee email (optional).")] string? assignedTo = null,
        [Description("Filter by lifecycle status: Open, InProgress, Blocked, Waiting, Done, Cancelled (optional).")] ItemStatus? status = null,
        CancellationToken ct = default)
    {
        var caller = Caller();

        var lists = new List<ListResponse>();
        if (listId is { } id)
        {
            var got = await _lists.GetAsync(caller, id, ct);
            if (got.IsOk) lists.Add(got.Value!);
        }
        else
        {
            lists.AddRange((await _lists.ListAsync(caller, archived: false, ct)).Value!.Lists);
            lists.AddRange((await _lists.ListAsync(caller, archived: true, ct)).Value!.Lists);
        }

        var results = new List<TaskSummary>();
        foreach (var list in lists)
        {
            var items = await _items.ListAsync(caller, list.Id, new ItemFilter(completed, null, null, assignedTo, status), ct);
            if (!items.IsOk) continue;
            foreach (var it in items.Value!.Items)
            {
                if (!string.IsNullOrWhiteSpace(query) && !it.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                results.Add(new TaskSummary(it.Id, list.Id, list.Name, it.Title, it.Status, it.Completed, it.DueAt, it.AssignedTo, it.Priority));
            }
        }
        return results;
    }

    [McpServerTool(Name = "add_task")]
    [Description("Add a task to a list. The current user must be an editor or owner of the list.")]
    public async Task<TaskSummary> AddTask(
        [Description("The list to add the task to.")] Guid listId,
        [Description("Task title.")] string title,
        [Description("Optional due date/time (ISO-8601).")] DateTimeOffset? dueAt = null,
        [Description("Optional assignee email.")] string? assignee = null,
        [Description("Optional quantity (useful for shopping lists).")] decimal? quantity = null,
        [Description("Optional unit, e.g. 'kg' (useful for shopping lists).")] string? unit = null,
        [Description("Optional priority 0..9 (0 = none, the default).")] int priority = 0,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var request = new CreateItemRequest
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            DueAt = dueAt,
            AssigneeEmail = assignee,
            Quantity = quantity,
            Unit = unit,
            Priority = priority,
            SortOrder = await NextSortOrderAsync(caller, listId, ct),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var item = Require(await _items.CreateAsync(caller, Guid.CreateVersion7(), listId, request, ct));
        return await ToTaskSummaryAsync(caller, listId, item, ct);
    }

    [McpServerTool(Name = "complete_task")]
    [Description("Mark a task complete.")]
    public Task<TaskSummary> CompleteTask(
        [Description("The task id.")] Guid taskId, CancellationToken ct = default) =>
        MutateAsync(taskId, (caller, listId) =>
            _items.CompleteAsync(caller, Guid.CreateVersion7(), listId, taskId, DateTimeOffset.UtcNow, ct), ct);

    [McpServerTool(Name = "reopen_task")]
    [Description("Reopen a previously completed task.")]
    public Task<TaskSummary> ReopenTask(
        [Description("The task id.")] Guid taskId, CancellationToken ct = default) =>
        MutateAsync(taskId, (caller, listId) =>
            _items.ReopenAsync(caller, Guid.CreateVersion7(), listId, taskId, DateTimeOffset.UtcNow, ct), ct);

    [McpServerTool(Name = "set_task_status")]
    [Description("Set a task's lifecycle status with an optional reason. Use Blocked/Waiting (with a reason) to record " +
        "stuck work, InProgress while working it, Cancelled to close without doing. Done is equivalent to completing it.")]
    public Task<TaskSummary> SetTaskStatus(
        [Description("The task id.")] Guid taskId,
        [Description("The new status: Open, InProgress, Blocked, Waiting, Done, or Cancelled.")] ItemStatus status,
        [Description("Optional reason (e.g. what it's blocked/waiting on).")] string? reason = null,
        CancellationToken ct = default) =>
        MutateAsync(taskId, (caller, listId) =>
            _items.SetStatusAsync(caller, Guid.CreateVersion7(), listId, taskId, status, reason, DateTimeOffset.UtcNow, ct), ct);

    [McpServerTool(Name = "update_task")]
    [Description("Update a task's fields. Only the arguments you pass are changed; omitted arguments are left as-is.")]
    public async Task<TaskSummary> UpdateTask(
        [Description("The task id.")] Guid taskId,
        [Description("New title (optional).")] string? title = null,
        [Description("New notes (optional).")] string? notes = null,
        [Description("New due date/time, ISO-8601 (optional).")] DateTimeOffset? dueAt = null,
        [Description("New assignee email (optional).")] string? assignee = null,
        [Description("New priority 0..9, 0 = none (optional; omitted leaves it unchanged).")] int? priority = null,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var listId = await _items.FindListIdAsync(taskId, ct)
            ?? throw new McpException($"No task found with id {taskId}.");
        var request = new UpdateItemRequest
        {
            Title = title,
            TitleProvided = title is not null,
            Notes = notes,
            NotesProvided = notes is not null,
            DueAt = dueAt,
            DueAtProvided = dueAt is not null,
            AssigneeEmail = assignee,
            AssigneeEmailProvided = assignee is not null,
            Priority = priority ?? 0,
            PriorityProvided = priority is not null,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var item = Require(await _items.UpdateAsync(caller, Guid.CreateVersion7(), listId, taskId, request, ct));
        return await ToTaskSummaryAsync(caller, listId, item, ct);
    }

    [McpServerTool(Name = "link_task")]
    [Description("Link a task to a cal-api Prompt heartbeat (toKind 'cal-item') or an external ref such as a GitHub " +
        "issue/PR, a health incident, or a release page (toKind 'url'). Use relationType 'monitors' for the checking " +
        "heartbeat of a standing monitor; others: 'spawned-by', 'produced', 'blocked-by', 'relates-to'. Idempotent.")]
    public async Task<RelationSummary> LinkTask(
        [Description("The task id.")] Guid taskId,
        [Description("What the task points at: 'cal-item' (a cal-api Prompt/event) or 'url' (an external reference).")] string toKind,
        [Description("The reference: a cal-api item id, or the URL/identifier of the external thing.")] string toRef,
        [Description("Relation type, e.g. 'monitors', 'spawned-by', 'produced', 'blocked-by', 'relates-to'.")] string relationType,
        [Description("Optional JSON object string for extra context (e.g. {\"note\":\"release watch\"}).")] string? metadata = null,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var listId = await _items.FindListIdAsync(taskId, ct)
            ?? throw new McpException($"No task found with id {taskId}.");
        var request = new CreateRelationRequest
        {
            ToKind = toKind,
            ToRef = toRef,
            RelationType = relationType,
            Metadata = ParseMetadata(metadata),
        };
        var rel = Require(await _relations.LinkAsync(caller, listId, taskId, request, ct));
        return new RelationSummary(rel.Id, rel.ToKind, rel.ToRef, rel.RelationType, rel.Metadata);
    }

    [McpServerTool(Name = "list_task_relations")]
    [Description("List a task's cross-API links (its cal-api heartbeat Prompt and any external refs).")]
    public async Task<IReadOnlyList<RelationSummary>> ListTaskRelations(
        [Description("The task id.")] Guid taskId, CancellationToken ct = default)
    {
        var caller = Caller();
        var listId = await _items.FindListIdAsync(taskId, ct)
            ?? throw new McpException($"No task found with id {taskId}.");
        var rels = Require(await _relations.ListAsync(caller, listId, taskId, ct));
        return rels.Select(r => new RelationSummary(r.Id, r.ToKind, r.ToRef, r.RelationType, r.Metadata)).ToList();
    }

    [McpServerTool(Name = "unlink_task")]
    [Description("Remove a task link identified by its edge tuple (toKind, toRef, relationType). Idempotent.")]
    public async Task<object> UnlinkTask(
        [Description("The task id.")] Guid taskId,
        [Description("The link's toKind (e.g. 'cal-item' or 'url').")] string toKind,
        [Description("The link's toRef.")] string toRef,
        [Description("The link's relationType.")] string relationType,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var listId = await _items.FindListIdAsync(taskId, ct)
            ?? throw new McpException($"No task found with id {taskId}.");
        var result = await _relations.UnlinkAsync(caller, listId, taskId, toKind, toRef, relationType, ct);
        if (result.Status != OpStatus.Ok)
            throw new McpException(result.Error ?? "Not found, or you don't have access to it.");
        return new { unlinked = true, taskId, toKind, toRef, relationType };
    }

    [McpServerTool(Name = "share_list")]
    [Description("Share a list with another family member by email (defaults to editor access).")]
    public async Task<ListSummary> ShareList(
        [Description("The list to share.")] Guid listId,
        [Description("Email of the member to add.")] string memberEmail,
        [Description("Role to grant: Owner, Editor, or Viewer (default Editor).")] ListRole role = ListRole.Editor,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var request = new AddMemberRequest { Email = memberEmail, Role = role };
        var updated = Require(await _lists.AddMemberAsync(caller, Guid.CreateVersion7(), listId, request, ct));
        return Summarize(caller, updated);
    }

    /// <summary>A public share link as the agent sees it (includes the opaque token + ready URL).</summary>
    public sealed record ShareLinkSummary(
        Guid ShareId, string Token, string Url, ShareAccess Access, string Label, DateTimeOffset? ExpiresAt, bool Revoked);

    [McpServerTool(Name = "create_share_link")]
    [Description("Create a public share link for a list (no account needed to open it). Read = view only; ReadWrite = full item editing. Owner only.")]
    public async Task<ShareLinkSummary> CreateShareLink(
        [Description("The list to share.")] Guid listId,
        [Description("Access level: Read or ReadWrite.")] ShareAccess access,
        [Description("Optional human label (used to attribute writes and shown in the owner's link list).")] string? label = null,
        [Description("Optional number of days until the link auto-expires.")] int? expiresInDays = null,
        CancellationToken ct = default)
    {
        var caller = Caller();
        DateTimeOffset? expiresAt = expiresInDays is { } d and > 0 ? DateTimeOffset.UtcNow.AddDays(d) : null;
        var request = new CreateShareRequest { Access = access, Label = label, ExpiresAt = expiresAt };
        return ToShareSummary(Require(await _shares.CreateAsync(caller, Guid.CreateVersion7(), listId, request, ct)));
    }

    [McpServerTool(Name = "list_share_links")]
    [Description("List the active public share links for a list (owner only).")]
    public async Task<IReadOnlyList<ShareLinkSummary>> ListShareLinks(
        [Description("The list.")] Guid listId, CancellationToken ct = default)
    {
        var caller = Caller();
        var result = Require(await _shares.ListAsync(caller, listId, ct));
        return result.Shares.Select(ToShareSummary).ToList();
    }

    [McpServerTool(Name = "revoke_share_link")]
    [Description("Revoke a public share link by its id (owner only). The link stops working immediately.")]
    public async Task<object> RevokeShareLink(
        [Description("The list.")] Guid listId,
        [Description("The share id (from create_share_link / list_share_links).")] Guid shareId,
        CancellationToken ct = default)
    {
        var caller = Caller();
        var result = await _shares.RevokeAsync(caller, Guid.CreateVersion7(), listId, shareId, ct);
        if (result.Status != OpStatus.Ok)
            throw new McpException(result.Error ?? "Not found, or you don't have access to it.");
        return new { revoked = true, shareId };
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private Caller Caller() => Application.Caller.Member(_user.RequireEmail(), _user.Groups);

    /// <summary>Parse an optional JSON string for a relation's free-form metadata, surfacing bad JSON as a tool error.</summary>
    private static JsonNode? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException ex) { throw new McpException($"`metadata` must be valid JSON: {ex.Message}"); }
    }

    /// <summary>Resolve the task's list (bare lookup), run the mutation (which re-checks membership), and summarize.</summary>
    private async Task<TaskSummary> MutateAsync(
        Guid taskId, Func<Caller, Guid, Task<OpResult<ItemResponse>>> op, CancellationToken ct)
    {
        var caller = Caller();
        var listId = await _items.FindListIdAsync(taskId, ct)
            ?? throw new McpException($"No task found with id {taskId}.");
        var item = Require(await op(caller, listId));
        return await ToTaskSummaryAsync(caller, listId, item, ct);
    }

    /// <summary>A trailing fractional-index sort key: just past the list's current max (or a base when empty).</summary>
    private async Task<string> NextSortOrderAsync(Caller caller, Guid listId, CancellationToken ct)
    {
        var items = await _items.ListAsync(caller, listId, new ItemFilter(null, null, null, null), ct);
        var maxSort = items.IsOk
            ? items.Value!.Items.Select(i => i.SortOrder).OrderBy(s => s, StringComparer.Ordinal).LastOrDefault()
            : null;
        // Appending a char yields a key strictly greater than the (prefix) max in ordinal order.
        return string.IsNullOrEmpty(maxSort) ? "a0" : maxSort + "5";
    }

    private async Task<TaskSummary> ToTaskSummaryAsync(Caller caller, Guid listId, ItemResponse item, CancellationToken ct)
    {
        var listName = (await _lists.GetAsync(caller, listId, ct)).Value?.Name ?? string.Empty;
        return new TaskSummary(item.Id, listId, listName, item.Title, item.Status, item.Completed, item.DueAt, item.AssignedTo, item.Priority);
    }

    private static ShareLinkSummary ToShareSummary(ShareResponse s) =>
        new(s.ShareId, s.Token, s.Url, s.Access, s.Label, s.ExpiresAt, s.Revoked);

    private static ListSummary Summarize(Caller caller, ListResponse list) =>
        new(list.Id, list.Name, list.Kind, RoleOf(caller, list), list.IsArchived, list.SimplePriority);

    private static ListRole? RoleOf(Caller caller, ListResponse list) =>
        list.Members.FirstOrDefault(m => string.Equals(m.Email, caller.Email, StringComparison.OrdinalIgnoreCase))?.Role;

    /// <summary>Unwrap a successful result or surface the failure to the agent as a tool error.</summary>
    private static T Require<T>(OpResult<T> result) => result.Status switch
    {
        OpStatus.Ok => result.Value!,
        OpStatus.NotFound => throw new McpException("Not found, or you don't have access to it."),
        OpStatus.Forbidden => throw new McpException(result.Error ?? "You don't have permission to do that."),
        OpStatus.Invalid => throw new McpException(result.Error ?? "The request was invalid."),
        _ => throw new McpException("Unexpected error."),
    };
}
