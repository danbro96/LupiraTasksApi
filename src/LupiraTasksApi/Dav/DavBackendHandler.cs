using LupiraTasksApi.Application;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Ical;
using Marten;

namespace LupiraTasksApi.Dav;

/// <summary>
/// The task-list (VTODO) half of the internal /dav-backend contract consumed by the LupiraDavApi gateway.
/// Acts on behalf of the principal named by the path {email} (the gateway verified the human credential via
/// LDAP Basic auth): the email is resolved (and JIT-provisioned) to an internal principal, with an empty
/// group set. VTODOs are regenerated from the snapshot with unmodeled properties spliced back from the last
/// PUT's raw blob; the assignee X-prop is resolved from the assignee principal id.
/// </summary>
public sealed class DavBackendHandler(IQuerySession session, TaskDavService dav, ListService lists, PrincipalDirectory principals)
{
    /// <summary>Resolve the acting DAV email (from the gateway-trusted path) to a member caller.</summary>
    private async Task<Caller> CallerFor(string email, CancellationToken ct)
    {
        var principal = await principals.ResolveOrProvisionAsync(sub: null, email, name: null, ct);
        return Caller.Member(principal.Id, principal.Email, []);
    }

    public async Task<IResult> CollectionsAsync(string email, CancellationToken ct)
    {
        var caller = await CallerFor(email, ct);
        var accessible = await lists.ListAsync(caller, archived: false, ct);
        if (accessible.Status != OpStatus.Ok) return TypedResults.Ok(Empty(email));

        var token = await dav.CurrentTokenAsync(ct);
        return TypedResults.Ok(new DavCollectionsDto
        {
            Principal = new DavPrincipalDto { DisplayName = email },
            Collections = [.. accessible.Value!.Lists.Select(l => new DavCollectionDto
            {
                Id = l.Id,
                Kind = DavCollectionKind.TodoList,
                DisplayName = l.Name,
                Ctag = $"seq-{token}",
                SyncToken = token.ToString(),
            })],
        });
    }

    public async Task<IResult> QueryAsync(string email, Guid collectionId, DavQueryRequest body, CancellationToken ct)
    {
        var caller = await CallerFor(email, ct);
        if (!await dav.CanReadAsync(caller, collectionId, ct)) return TypedResults.NotFound();

        var list = await session.LoadAsync<TodoList>(collectionId, ct);
        var live = await dav.ItemsAsync(collectionId, ct);
        IEnumerable<Item> selected = live;
        if (body.Uids is { Count: > 0 } uids)
        {
            var set = uids.ToHashSet(StringComparer.Ordinal);
            selected = live.Where(i => set.Contains(i.Uid));
        }
        // Start/End: DAVx5 doesn't time-range VTODOs — the window is ignored in v1 (full listing).

        var selectedList = selected.ToList();
        var assignees = await principals.LookupAsync(
            selectedList.Where(i => i.AssignedToPrincipalId is not null).Select(i => i.AssignedToPrincipalId!.Value), ct);

        return TypedResults.Ok(new DavResourcesDto
        {
            Resources = [.. selectedList.Select(i => new DavResourceDto
            {
                Uid = i.Uid,
                Etag = TaskDavService.Etag(i),
                Content = body.IncludeContent ? VtodoMapper.ToVtodo(i, list!.Tags, i.SourceVtodo, AssigneeEmail(i, assignees)) : null,
            })],
        });
    }

    public async Task<IResult> GetResourceAsync(string email, Guid collectionId, string uid, HttpContext ctx, CancellationToken ct)
    {
        var caller = await CallerFor(email, ct);
        if (!await dav.CanReadAsync(caller, collectionId, ct)) return TypedResults.NotFound();

        var item = await dav.FindAsync(collectionId, uid, ct);
        if (item is null) return TypedResults.NotFound();

        var list = await session.LoadAsync<TodoList>(collectionId, ct);
        var assignees = item.AssignedToPrincipalId is { } a
            ? await principals.LookupAsync([a], ct)
            : null;
        ctx.Response.Headers.ETag = $"\"{TaskDavService.Etag(item)}\"";
        return TypedResults.Text(
            VtodoMapper.ToVtodo(item, list!.Tags, item.SourceVtodo, AssigneeEmail(item, assignees)),
            "text/calendar; charset=utf-8");
    }

    public async Task<IResult> PutResourceAsync(string email, Guid collectionId, string uid, HttpContext ctx, CancellationToken ct)
    {
        var caller = await CallerFor(email, ct);
        using var reader = new StreamReader(ctx.Request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        var (ifMatch, ifNoneMatchStar) = ParsePreconditions(ctx.Request.Headers.IfMatch, ctx.Request.Headers.IfNoneMatch);

        var result = await dav.PutVtodoAsync(caller, collectionId, uid, raw, ifMatch, ifNoneMatchStar, ct);
        if (result.Status == OpStatus.Ok && result.Value is { } w)
        {
            ctx.Response.Headers.ETag = $"\"{w.Etag}\"";
            return TypedResults.StatusCode(w.Created ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
        }
        return TypedResults.StatusCode(DavStatus(result.Status));
    }

    public async Task<IResult> DeleteResourceAsync(string email, Guid collectionId, string uid, HttpContext ctx, CancellationToken ct)
    {
        var caller = await CallerFor(email, ct);
        var (ifMatch, _) = ParsePreconditions(ctx.Request.Headers.IfMatch, ctx.Request.Headers.IfNoneMatch);
        var result = await dav.DeleteByUidAsync(caller, collectionId, uid, ifMatch, ct);
        return TypedResults.StatusCode(DavStatus(result.Status));
    }

    public async Task<IResult> ChangesAsync(string email, Guid collectionId, string? since, CancellationToken ct)
    {
        var caller = await CallerFor(email, ct);
        // An unparsable/absent token degrades to the full live listing — self-healing resync.
        long? parsed = long.TryParse(since, out var t) ? t : null;
        var result = await dav.ChangesSinceAsync(caller, collectionId, parsed, ct);
        if (result.Status != OpStatus.Ok) return TypedResults.NotFound();

        var (token, changes) = (result.Value!.Token, result.Value.Changes);
        return TypedResults.Ok(new DavChangesDto
        {
            SyncToken = token.ToString(),
            Changed = [.. changes.Where(c => !c.Deleted).Select(c => new DavChangeDto { Uid = c.Uid, Etag = c.Etag! })],
            Deleted = [.. changes.Where(c => c.Deleted).Select(c => c.Uid)],
        });
    }

    /// <summary>The assignee's email for the VTODO X-prop, resolved from the item's assignee principal id.</summary>
    private static string? AssigneeEmail(Item item, IReadOnlyDictionary<Guid, Principal>? assignees) =>
        item.AssignedToPrincipalId is { } a && assignees is not null && assignees.TryGetValue(a, out var p)
            ? p.Email
            : null;

    private static DavCollectionsDto Empty(string email) => new()
    {
        Principal = new DavPrincipalDto { DisplayName = email },
        Collections = [],
    };

    /// <summary>An <c>If-Match</c> of <c>*</c> (or empty) is "no specific tag"; quotes are stripped from a
    /// concrete tag. <c>If-None-Match: *</c> is the "create only if absent" guard.</summary>
    internal static (string? IfMatch, bool IfNoneMatchStar) ParsePreconditions(string? ifMatchHeader, string? ifNoneMatchHeader)
    {
        string? ifMatch = null;
        var im = ifMatchHeader?.Trim();
        if (!string.IsNullOrEmpty(im) && im != "*") ifMatch = im.Trim('"');
        var inm = ifNoneMatchHeader?.Trim() ?? "";
        return (ifMatch, inm == "*");
    }

    internal static int DavStatus(OpStatus status) => status switch
    {
        OpStatus.Ok => StatusCodes.Status204NoContent,
        OpStatus.Forbidden => StatusCodes.Status403Forbidden,
        OpStatus.NotFound => StatusCodes.Status404NotFound,
        OpStatus.Conflict => StatusCodes.Status412PreconditionFailed,
        OpStatus.Invalid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };
}
