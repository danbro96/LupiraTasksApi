using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lupira.Mcp;
using Lupira.Results;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Core.Application;
using LupiraTasksApi.Core.Application.Items;
using LupiraTasksApi.Core.Application.Lists;
using LupiraTasksApi.Core.Application.Shares;
using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Dtos.Items;
using LupiraTasksApi.Core.Dtos.Lists;
using LupiraTasksApi.Core.Dtos.Relations;
using LupiraTasksApi.Core.Dtos.Shares;
using ModelContextProtocol;
using ModelContextProtocol.Server;

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
    private readonly CallerFactory _callers;
    private readonly ListService _lists;
    private readonly ItemService _items;
    private readonly RelationService _relations;
    private readonly ShareService _shares;

    public TaskTools(CallerFactory callers, ListService lists, ItemService items, RelationService relations, ShareService shares)
    {
        _callers = callers;
        _lists = lists;
        _items = items;
        _relations = relations;
        _shares = shares;
    }

    /// <summary>A list as the agent sees it — trimmed, with the caller's own role.</summary>
    public sealed record ListSummary(Guid Id, string Name, ListKind Kind, ListRole? Role, bool IsArchived, bool SimplePriority);

    /// <summary>A task as the agent sees it — trimmed, with its owning list named and its parent for nesting.</summary>
    public sealed record TaskSummary(
        Guid Id, Guid ListId, string ListName, Guid? ParentTaskId, string Title, ItemStatus Status, bool Completed, DateTimeOffset? DueAt, string? AssignedTo, int Priority, JsonObject? Metadata);

    /// <summary>A task created by <c>add_tasks_batch</c> — just enough to follow it up or check the tree.</summary>
    public sealed record CreatedTask(Guid Id, Guid? ParentTaskId, string Title);

    /// <summary>A cross-API link as the agent sees it — the edge tuple needed to read or unlink it.</summary>
    public sealed record RelationSummary(Guid Id, string ToKind, string ToRef, string RelationType, JsonObject? Metadata);

    [McpServerTool(Name = "list_my_lists")]
    [Description("List the to-do / shopping lists the current user is a member of, with their role on each.")]
    public async Task<IReadOnlyList<ListSummary>> ListMyLists(
        [Description("Include archived lists as well (default false).")] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        var lists = (await _lists.ListAsync(caller, archived: false, ct)).Value!.ToList();
        if (includeArchived)
            lists.AddRange((await _lists.ListAsync(caller, archived: true, ct)).Value!);
        return lists.Select(l => Summarize(caller, l)).ToList();
    }

    [McpServerTool(Name = "create_list")]
    [Description("Create a new list owned by the current user.")]
    public async Task<ListSummary> CreateList(
        [Description("Display name of the list.")] string name,
        [Description("List kind: Todo, Shopping, or Agent (default Todo). Use Agent for the assistant's own backlog or operator/ops lists.")] ListKind kind = ListKind.Todo,
        CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        var request = new CreateListRequest { Id = Guid.CreateVersion7(), Name = name, Kind = kind };
        var created = (await _lists.CreateAsync(caller, Guid.CreateVersion7(), request, ct)).Require();
        return Summarize(caller, created);
    }

    [McpServerTool(Name = "search_tasks")]
    [Description("Find tasks across the user's lists. Optionally scope to one list, or filter by a title substring, " +
        "completion state, assignee, lifecycle status (e.g. Blocked/Waiting to see what's stuck), or position in the " +
        "task tree. Every result carries its parentTaskId, so a whole nested list can be reconstructed from one call.")]
    public async Task<IReadOnlyList<TaskSummary>> SearchTasks(
        [Description("Restrict to a single list id (optional; searches all the user's lists when omitted).")] Guid? listId = null,
        [Description("Case-insensitive substring to match in the task title (optional).")] string? query = null,
        [Description("Filter by completion state (optional).")] bool? completed = null,
        [Description("Filter by assignee email (optional).")] string? assignedTo = null,
        [Description("Filter by lifecycle status: Open, InProgress, Blocked, Waiting, Done, Cancelled (optional).")] ItemStatus? status = null,
        [Description("Return only the direct subtasks of this task (optional).")] Guid? parentTaskId = null,
        [Description("Return only top-level tasks, i.e. those with no parent (optional; ignored when parentTaskId is given).")] bool rootsOnly = false,
        CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);

        var lists = new List<ListDto>();
        if (listId is { } id)
        {
            var got = await _lists.GetAsync(caller, id, ct);
            if (got.IsOk) lists.Add(got.Value!);
        }
        else
        {
            lists.AddRange((await _lists.ListAsync(caller, archived: false, ct)).Value!);
            lists.AddRange((await _lists.ListAsync(caller, archived: true, ct)).Value!);
        }

        var results = new List<TaskSummary>();
        foreach (var list in lists)
        {
            var items = await _items.ListAsync(caller, list.Id, new ItemFilter(completed, null, parentTaskId, assignedTo, status), ct);
            if (!items.IsOk) continue;
            foreach (var it in items.Value!)
            {
                if (!string.IsNullOrWhiteSpace(query) && !it.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (rootsOnly && parentTaskId is null && it.ParentItemId is not null)
                    continue;
                results.Add(Summarize(list, it));
            }
        }

        return results;
    }

    [McpServerTool(Name = "add_task")]
    [Description("Add a task to a list, optionally as a subtask of another task in the same list. Appended after the " +
        "existing tasks at its level. The current user must be an editor or owner of the list.")]
    public async Task<TaskSummary> AddTask(
        [Description("The list to add the task to.")] Guid listId,
        [Description("Task title.")] string title,
        [Description("Make this a subtask of that task (optional). Must be a task in the same list; omit for a top-level task.")] Guid? parentTaskId = null,
        [Description("Optional due date/time (ISO-8601).")] DateTimeOffset? dueAt = null,
        [Description("Optional assignee email.")] string? assignee = null,
        [Description("Optional quantity (useful for shopping lists).")] decimal? quantity = null,
        [Description("Optional unit, e.g. 'kg' (useful for shopping lists).")] string? unit = null,
        [Description("Optional priority 0..9 (0 = none, the default).")] int priority = 0,
        CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        var siblings = await ItemsAsync(caller, listId, ct);
        if (parentTaskId is { } parent) await RequireInListAsync(siblings, parent, ct);

        var request = new CreateItemRequest
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            ParentItemId = parentTaskId,
            DueAt = dueAt,
            AssigneeEmail = assignee,
            Quantity = quantity,
            Unit = unit,
            Priority = priority,
            SortOrder = FractionalIndex.KeyAfter(MaxSortKey(siblings.Where(i => i.ParentItemId == parentTaskId))),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var item = (await _items.CreateAsync(caller, Guid.CreateVersion7(), listId, request, ct)).Require();
        return await ToTaskSummaryAsync(caller, listId, item, ct);
    }

    [McpServerTool(Name = "add_tasks_batch")]
    [Description("Add many tasks to one list in a single call, nested to any depth through each task's `subtasks` — " +
        "the shape the app's list export/import round-trips, so a whole structured list (a recipe's dishes and their " +
        "ingredients, a project's phases and steps) can be built in one go. Tasks are appended after the list's " +
        "existing tasks, in the order given. At most 500 tasks, nested at most 5 levels deep, per call.")]
    public async Task<IReadOnlyList<CreatedTask>> AddTasksBatch(
        [Description("The list to add the tasks to.")] Guid listId,
        [Description("The tasks to add, in order. Each is {title, subtasks?}.")] IReadOnlyList<TaskNode> tasks,
        CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        // One read of the list, then one ascending key chain over the whole tree — no re-read per insert.
        var planned = TaskTree.Plan(tasks, MaxSortKey(await ItemsAsync(caller, listId, ct)));

        var created = new List<CreatedTask>(planned.Count);
        foreach (var task in planned)
        {
            var request = new CreateItemRequest
            {
                Id = task.Id,
                Title = task.Title,
                ParentItemId = task.ParentId,
                SortOrder = task.SortOrder,
                OccurredAt = DateTimeOffset.UtcNow,
            };
            var item = (await _items.CreateAsync(caller, Guid.CreateVersion7(), listId, request, ct)).Require();
            created.Add(new CreatedTask(item.Id, item.ParentItemId, item.Title));
        }

        return created;
    }

    [McpServerTool(Name = "move_task")]
    [Description("Reparent and/or reorder a task within its list. Pass parentTaskId to make it a subtask of that " +
        "task, or omit it to promote the task to the top level. For the position: afterTaskId puts it directly after " +
        "that sibling, atStart puts it first, and passing neither appends it after its siblings.")]
    public async Task<TaskSummary> MoveTask(
        [Description("The task to move.")] Guid taskId,
        [Description("The task's new parent, in the same list (optional). Omit to move it to the top level.")] Guid? parentTaskId = null,
        [Description("Place the task directly after this sibling (optional; must sit under the new parent).")] Guid? afterTaskId = null,
        [Description("Place the task first among its siblings (optional; ignored when afterTaskId is given).")] bool atStart = false,
        CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        var listId = await ListOfAsync(taskId, ct);
        var items = await ItemsAsync(caller, listId, ct);

        if (parentTaskId is { } parent)
        {
            if (parent == taskId) throw new McpException("A task cannot be its own parent.");
            await RequireInListAsync(items, parent, ct);
            if (Descendants(items, taskId).Contains(parent))
                throw new McpException($"Task {parent} is below {taskId} in the tree — that move would make a cycle.");
        }

        // Siblings under the NEW parent, excluding the task itself (a reorder within its own group).
        var siblings = items
            .Where(i => i.Id != taskId && i.ParentItemId == parentTaskId && FractionalIndex.IsValid(i.SortOrder))
            .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
            .ToList();

        var request = new MoveItemRequest
        {
            ParentItemId = parentTaskId,
            SortOrder = PositionKey(siblings, afterTaskId, atStart),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var item = (await _items.MoveAsync(caller, Guid.CreateVersion7(), listId, taskId, request, ct)).Require();
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

    [McpServerTool(Name = "set_task_metadata")]
    [Description("Set a task's free-form JSON metadata for agent bookkeeping (e.g. source-alert id, check count, " +
        "last-result summary). Server-side only — never shown to share-link viewers or in CalDAV. Pass null/empty to clear.")]
    public Task<TaskSummary> SetTaskMetadata(
        [Description("The task id.")] Guid taskId,
        [Description("A JSON object string, e.g. {\"alertId\":\"abc\",\"checks\":3}. Null or empty clears it.")] string? metadataJson = null,
        CancellationToken ct = default)
    {
        var metadata = ParseMetadata(metadataJson)?.ToJsonString();
        return MutateAsync(taskId, (caller, listId) =>
            _items.SetMetadataAsync(caller, Guid.CreateVersion7(), listId, taskId, metadata, DateTimeOffset.UtcNow, ct), ct);
    }

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
        var caller = await CallerAsync(ct);
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
        var item = (await _items.UpdateAsync(caller, Guid.CreateVersion7(), listId, taskId, request, ct)).Require();
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
        var caller = await CallerAsync(ct);
        var listId = await _items.FindListIdAsync(taskId, ct)
            ?? throw new McpException($"No task found with id {taskId}.");
        var request = new CreateRelationRequest
        {
            ToKind = toKind,
            ToRef = toRef,
            RelationType = relationType,
            Metadata = ParseMetadata(metadata),
        };
        var rel = (await _relations.LinkAsync(caller, listId, taskId, request, ct)).Require();
        return new RelationSummary(rel.Id, rel.ToKind, rel.ToRef, rel.RelationType, rel.Metadata);
    }

    [McpServerTool(Name = "list_task_relations")]
    [Description("List a task's cross-API links (its cal-api heartbeat Prompt and any external refs).")]
    public async Task<IReadOnlyList<RelationSummary>> ListTaskRelations(
        [Description("The task id.")] Guid taskId, CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        var listId = await _items.FindListIdAsync(taskId, ct)
            ?? throw new McpException($"No task found with id {taskId}.");
        var rels = (await _relations.ListAsync(caller, listId, taskId, ct)).Require();
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
        var caller = await CallerAsync(ct);
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
        var caller = await CallerAsync(ct);
        var request = new AddMemberRequest { Email = memberEmail, Role = role };
        var updated = (await _lists.AddMemberAsync(caller, Guid.CreateVersion7(), listId, request, ct)).Require();
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
        var caller = await CallerAsync(ct);
        DateTimeOffset? expiresAt = expiresInDays is { } d and > 0 ? DateTimeOffset.UtcNow.AddDays(d) : null;
        var request = new CreateShareRequest { Access = access, Label = label, ExpiresAt = expiresAt };
        return ToShareSummary((await _shares.CreateAsync(caller, Guid.CreateVersion7(), listId, request, ct)).Require());
    }

    [McpServerTool(Name = "list_share_links")]
    [Description("List the active public share links for a list (owner only).")]
    public async Task<IReadOnlyList<ShareLinkSummary>> ListShareLinks(
        [Description("The list.")] Guid listId, CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        var result = (await _shares.ListAsync(caller, listId, ct)).Require();
        return result.Select(ToShareSummary).ToList();
    }

    [McpServerTool(Name = "revoke_share_link")]
    [Description("Revoke a public share link by its id (owner only). The link stops working immediately.")]
    public async Task<object> RevokeShareLink(
        [Description("The list.")] Guid listId,
        [Description("The share id (from create_share_link / list_share_links).")] Guid shareId,
        CancellationToken ct = default)
    {
        var caller = await CallerAsync(ct);
        var result = await _shares.RevokeAsync(caller, Guid.CreateVersion7(), listId, shareId, ct);
        if (result.Status != OpStatus.Ok)
            throw new McpException(result.Error ?? "Not found, or you don't have access to it.");
        return new { revoked = true, shareId };
    }

    private async Task<Caller> CallerAsync(CancellationToken ct) =>
        await _callers.MemberAsync(ct) ?? throw new McpException("Unauthenticated.");

    /// <summary>Parse an optional JSON string for free-form metadata, surfacing anything but a JSON object as a tool error.</summary>
    private static JsonObject? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) switch
            {
                null => null,
                JsonObject obj => obj,
                _ => throw new McpException("`metadata` must be a JSON object."),
            };
        }
        catch (JsonException)
        {
            throw new McpException("`metadata` must be a JSON object.");
        }
    }

    /// <summary>Resolve the task's list (bare lookup), run the mutation (which re-checks membership), and summarize.</summary>
    private async Task<TaskSummary> MutateAsync(
        Guid taskId, Func<Caller, Guid, Task<OpResult<ItemDto>>> op, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        var listId = await ListOfAsync(taskId, ct);
        var item = (await op(caller, listId)).Require();
        return await ToTaskSummaryAsync(caller, listId, item, ct);
    }

    /// <summary>The list a task belongs to (bare lookup — the mutation that follows enforces membership).</summary>
    private async Task<Guid> ListOfAsync(Guid taskId, CancellationToken ct) =>
        await _items.FindListIdAsync(taskId, ct) ?? throw new McpException($"No task found with id {taskId}.");

    /// <summary>Every live task of a list, in sort order — the working set for the tree/sibling maths.</summary>
    private async Task<IReadOnlyList<ItemDto>> ItemsAsync(Caller caller, Guid listId, CancellationToken ct) =>
        (await _items.ListAsync(caller, listId, new ItemFilter(null, null, null, null), ct)).Require();

    /// <summary>
    /// Require a referenced task to be in the same list. The services only guard self-parenting — referential
    /// existence is deliberately not a command-time invariant (it would fight the offline replay model, where a
    /// child can arrive before its parent), so this online-only surface pre-flights it instead. Checked against
    /// the list's readable items, so it can't be used to probe for tasks the caller has no access to.
    /// </summary>
    private async Task RequireInListAsync(IReadOnlyList<ItemDto> items, Guid taskId, CancellationToken ct)
    {
        if (items.Any(i => i.Id == taskId)) return;
        throw new McpException(await _items.FindListIdAsync(taskId, ct) is null
            ? $"No task found with id {taskId}."
            : $"Task {taskId} is in a different list — related tasks must share a list.");
    }

    /// <summary>All tasks below <paramref name="taskId"/> in the tree (children, grandchildren, …).</summary>
    private static HashSet<Guid> Descendants(IReadOnlyList<ItemDto> items, Guid taskId)
    {
        var found = new HashSet<Guid>();
        var frontier = new Queue<Guid>([taskId]);
        while (frontier.TryDequeue(out var id))
        {
            foreach (var child in items.Where(i => i.ParentItemId == id).Select(i => i.Id))
                if (found.Add(child)) frontier.Enqueue(child);
        }

        return found;
    }

    /// <summary>The sort key that puts a task at the requested position among <paramref name="siblings"/>
    /// (ordered, excluding the task itself): after a named one, first, or last.</summary>
    private static string PositionKey(IReadOnlyList<ItemDto> siblings, Guid? afterTaskId, bool atStart)
    {
        if (afterTaskId is { } after)
        {
            var index = siblings.ToList().FindIndex(i => i.Id == after);
            if (index < 0) throw new McpException($"Task {after} is not among the siblings the task is moving to.");
            var key = siblings[index].SortOrder;
            // Skip any sibling sharing that key (concurrent inserts can collide) so the bound is a real upper one.
            var upper = siblings.Skip(index + 1).Select(i => i.SortOrder)
                .FirstOrDefault(k => string.CompareOrdinal(k, key) > 0);
            return FractionalIndex.KeyBetween(key, upper);
        }

        return atStart
            ? FractionalIndex.KeyBetween(null, siblings.Count > 0 ? siblings[0].SortOrder : null)
            : FractionalIndex.KeyAfter(siblings.Count > 0 ? siblings[^1].SortOrder : null);
    }

    /// <summary>The highest well-formed sort key among <paramref name="items"/>, or <c>null</c> when there is none.
    /// The DAV seam mints <c>'~'+guid</c> keys, which aren't fractional indices; they sort after every base-62 key,
    /// so skipping them appends to the end of the interleavable chain.</summary>
    private static string? MaxSortKey(IEnumerable<ItemDto> items) =>
        items.Select(i => i.SortOrder)
            .Where(FractionalIndex.IsValid)
            .OrderBy(s => s, StringComparer.Ordinal)
            .LastOrDefault();

    private async Task<TaskSummary> ToTaskSummaryAsync(Caller caller, Guid listId, ItemDto item, CancellationToken ct)
    {
        var listName = (await _lists.GetAsync(caller, listId, ct)).Value?.Name ?? string.Empty;
        return new TaskSummary(item.Id, listId, listName, item.ParentItemId, item.Title, item.Status, item.Completed, item.DueAt, item.Assignee?.Email, item.Priority, item.Metadata);
    }

    private static TaskSummary Summarize(ListDto list, ItemDto item) =>
        new(item.Id, list.Id, list.Name, item.ParentItemId, item.Title, item.Status, item.Completed, item.DueAt, item.Assignee?.Email, item.Priority, item.Metadata);

    private static ShareLinkSummary ToShareSummary(ShareDto s) =>
        new(s.ShareId, s.Token, s.Url, s.Access, s.Label, s.ExpiresAt, s.Revoked);

    private static ListSummary Summarize(Caller caller, ListDto list) =>
        new(list.Id, list.Name, list.Kind, RoleOf(caller, list), list.IsArchived, list.SimplePriority);

    private static ListRole? RoleOf(Caller caller, ListDto list) =>
        list.Members.FirstOrDefault(m => m.PrincipalId == caller.PrincipalId)?.Role;
}
