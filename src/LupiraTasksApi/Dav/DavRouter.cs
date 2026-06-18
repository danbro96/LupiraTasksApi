using System.Globalization;
using System.Xml.Linq;
using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Ical;
using Marten;

namespace LupiraTasksApi.Dav;

/// <summary>
/// CalDAV (RFC 4791) for tasks (VTODO) over the Marten <c>Item</c>/<c>TodoList</c> streams — the surface
/// DAVx5 syncs into Android task apps (tasks.org / OpenTasks). Reads come from the inline snapshots; the
/// VTODO body is <em>regenerated</em> from the live item on GET/REPORT (the REST/MCP surfaces edit fields
/// granularly, so a stored blob would go stale), splicing back unmodeled properties from the last PUT.
/// Writes append events via <see cref="TaskDavService"/>. The ctag + sync-token derive from Marten's
/// global event <c>Sequence</c> (opaque, monotonic); the per-item ETag is the stream <c>Version</c>.
///
/// URL layout (all discovered, never typed):
///   /dav/ → root; /dav/u/{email}/ → principal; /dav/u/{email}/lists/{listId}/ → a task list (VTODO collection);
///   /dav/u/{email}/lists/{listId}/{uid}.ics → a task. A principal addresses only its own /u/ tree.
///
/// Structure mirrors LupiraCalApi's DavRouter, reduced to a single (tasks) track.
/// </summary>
public static class DavRouter
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";

    public static async Task Handle(HttpContext ctx)
    {
        var method = ctx.Request.Method.ToUpperInvariant();
        var requestCt = ctx.RequestAborted;

        if (method == "OPTIONS") { WriteOptions(ctx); return; }
        if (method is "MKCALENDAR" or "MKCOL" or "PROPPATCH" or "MOVE" or "COPY" or "LOCK" or "UNLOCK")
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var current = ctx.RequestServices.GetRequiredService<CurrentUser>();
        var email = current.RequireEmail();
        var caller = Caller.Member(email, current.Groups);

        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var segments = (ctx.Request.Path.Value ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rest = segments.Skip(1).ToArray();   // segments[0] == "dav"

        // A principal addresses only its own /u/{email}/ tree.
        if (rest.Length >= 2 && rest[0] == "u" && !string.Equals(rest[1], email, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var depth = ctx.Request.Headers.TryGetValue("Depth", out var dh) ? dh.ToString() : "0";
        var deep = depth is "1" or "infinity";

        if (rest.Length == 0)
        {
            if (method == "PROPFIND") { await WriteMultiStatus(ctx, RootPropfind(baseUrl, email)); return; }
        }
        else if (rest[0] == "u" && rest.Length >= 2)
        {
            if (rest.Length == 2 && method == "PROPFIND") { await WriteMultiStatus(ctx, PrincipalPropfind(baseUrl, email, current.DisplayName)); return; }

            if (rest.Length >= 3 && rest[2] == "lists")
            {
                if (rest.Length == 3 && method == "PROPFIND")
                {
                    await WriteMultiStatus(ctx, await ListsHomePropfind(ctx, baseUrl, email, caller, deep, requestCt));
                    return;
                }
                if (rest.Length == 4 && Guid.TryParse(rest[3], out var listId))
                {
                    if (method == "PROPFIND") { await WriteMultiStatus(ctx, await ListPropfind(ctx, baseUrl, email, caller, listId, deep, requestCt)); return; }
                    if (method == "REPORT") { await HandleReport(ctx, baseUrl, email, caller, listId); return; }
                }
                if (rest.Length == 5 && Guid.TryParse(rest[3], out var lid))
                {
                    var uid = DavProtocol.StripExt(rest[4]);
                    if (method is "GET" or "HEAD") { await GetItem(ctx, caller, lid, uid); return; }
                    if (method == "PUT") { await HandlePut(ctx, caller, lid, uid); return; }
                    if (method == "DELETE") { await HandleDelete(ctx, caller, lid, uid); return; }
                }
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    // ---------- token (opaque, monotonic) = Marten's current global event sequence ----------

    private static async Task<long> CurrentTokenAsync(IQuerySession session, CancellationToken ct)
    {
        var last = await session.Events.QueryAllRawEvents().OrderByDescending(e => e.Sequence).Take(1).ToListAsync(ct);
        return last.Count > 0 ? last[0].Sequence : 0L;
    }

    // ---------- PROPFIND builders ----------

    private static XElement RootPropfind(string baseUrl, string email) => MultiStatus(
        Response($"{baseUrl}/dav/",
            new XElement(D + "resourcetype", new XElement(D + "collection")),
            new XElement(D + "current-user-principal", Href($"{baseUrl}/dav/u/{Seg(email)}/"))));

    private static XElement PrincipalPropfind(string baseUrl, string email, string? displayName) => MultiStatus(
        Response($"{baseUrl}/dav/u/{Seg(email)}/",
            new XElement(D + "resourcetype", new XElement(D + "collection"), new XElement(D + "principal")),
            new XElement(D + "displayname", displayName ?? email),
            new XElement(D + "current-user-principal", Href($"{baseUrl}/dav/u/{Seg(email)}/")),
            new XElement(D + "principal-URL", Href($"{baseUrl}/dav/u/{Seg(email)}/")),
            new XElement(C + "calendar-home-set", Href($"{baseUrl}/dav/u/{Seg(email)}/lists/"))));

    private static async Task<XElement> ListsHomePropfind(HttpContext ctx, string baseUrl, string email, Caller caller, bool deep, CancellationToken ct)
    {
        var responses = new List<XElement>
        {
            Response($"{baseUrl}/dav/u/{Seg(email)}/lists/", new XElement(D + "resourcetype", new XElement(D + "collection"))),
        };
        if (deep)
        {
            var session = ctx.RequestServices.GetRequiredService<IQuerySession>();
            var lists = ctx.RequestServices.GetRequiredService<ListService>();
            var result = await lists.ListAsync(caller, archived: false, ct);
            var token = await CurrentTokenAsync(session, ct);
            if (result.IsOk)
                foreach (var l in result.Value!.Lists)
                    responses.Add(Response($"{baseUrl}/dav/u/{Seg(email)}/lists/{l.Id}/", ListCollectionProps(l.Name, token)));
        }
        return MultiStatus([.. responses]);
    }

    private static async Task<XElement> ListPropfind(HttpContext ctx, string baseUrl, string email, Caller caller, Guid listId, bool deep, CancellationToken ct)
    {
        var access = ctx.RequestServices.GetRequiredService<AccessResolver>();
        var acc = await access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct);
        if (!acc.Allowed) return MultiStatus();

        var session = ctx.RequestServices.GetRequiredService<IQuerySession>();
        var token = await CurrentTokenAsync(session, ct);
        var responses = new List<XElement> { Response($"{baseUrl}/dav/u/{Seg(email)}/lists/{listId}/", ListCollectionProps(acc.List!.Name, token)) };
        if (deep)
        {
            var dav = ctx.RequestServices.GetRequiredService<TaskDavService>();
            foreach (var i in await dav.ItemsAsync(listId, ct))
                responses.Add(Response($"{baseUrl}/dav/u/{Seg(email)}/lists/{listId}/{Seg(i.Uid)}.ics",
                    new XElement(D + "getetag", Etag(i)),
                    new XElement(D + "getcontenttype", "text/calendar; charset=utf-8")));
        }
        return MultiStatus([.. responses]);
    }

    private static XElement[] ListCollectionProps(string name, long token) =>
    [
        new XElement(D + "resourcetype", new XElement(D + "collection"), new XElement(C + "calendar")),
        new XElement(D + "displayname", name),
        new XElement(CS + "getctag", $"\"seq-{token}\""),
        new XElement(D + "sync-token", $"{token}"),
        new XElement(C + "supported-calendar-component-set", new XElement(C + "comp", new XAttribute("name", "VTODO"))),
        SupportedReports(C + "calendar-query", C + "calendar-multiget", D + "sync-collection"),
    ];

    // ---------- REPORT ----------

    private static async Task HandleReport(HttpContext ctx, string baseUrl, string email, Caller caller, Guid listId)
    {
        var ct = ctx.RequestAborted;
        var access = ctx.RequestServices.GetRequiredService<AccessResolver>();
        var acc = await access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct);
        if (!acc.Allowed) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var body = await ReadBody(ctx);
        var doc = DavProtocol.TryParseXml(body);
        if (doc?.Root?.Name == D + "sync-collection") { await Sync(ctx, baseUrl, email, listId, doc); return; }

        var dav = ctx.RequestServices.GetRequiredService<TaskDavService>();
        var items = await dav.ItemsAsync(listId, ct);
        var requested = DavProtocol.ExtractHrefUids(body, ".ics");
        if (requested.Count > 0) items = [.. items.Where(i => requested.Contains(i.Uid))];   // calendar-multiget

        var tags = acc.List!.Tags;
        var responses = items.Select(i => Response($"{baseUrl}/dav/u/{Seg(email)}/lists/{listId}/{Seg(i.Uid)}.ics",
            new XElement(D + "getetag", Etag(i)),
            new XElement(C + "calendar-data", VtodoMapper.ToVtodo(i, tags, i.SourceVtodo))));
        await WriteMultiStatus(ctx, MultiStatus([.. responses]));
    }

    private static async Task Sync(HttpContext ctx, string baseUrl, string email, Guid listId, XDocument doc)
    {
        var ct = ctx.RequestAborted;
        var session = ctx.RequestServices.GetRequiredService<IQuerySession>();
        var token = DavProtocol.ParseSyncToken(doc);
        var newToken = await CurrentTokenAsync(session, ct);
        var responses = new List<XElement>();
        string Link(string uid) => $"{baseUrl}/dav/u/{Seg(email)}/lists/{listId}/{Seg(uid)}.ics";

        if (token is null)   // initial sync: every live resource in the list
        {
            var dav = ctx.RequestServices.GetRequiredService<TaskDavService>();
            foreach (var i in await dav.ItemsAsync(listId, ct))
                responses.Add(Response(Link(i.Uid), new XElement(D + "getetag", Etag(i))));
        }
        else                 // diffs since token: items in this list whose stream changed
        {
            var changedIds = (await session.Events.QueryAllRawEvents().Where(e => e.Sequence > token).ToListAsync(ct))
                .Select(e => e.StreamId).Distinct().ToList();
            var items = await session.Query<Item>().Where(i => changedIds.Contains(i.Id)).ToListAsync(ct);
            foreach (var i in items)
            {
                if (i.ListId != listId) continue;   // never been in this list
                responses.Add(i.Deleted
                    ? DeletedResponse(Link(i.Uid))
                    : Response(Link(i.Uid), new XElement(D + "getetag", Etag(i))));
            }
        }
        await WriteMultiStatus(ctx, MultiStatusWithToken(newToken, [.. responses]));
    }

    // ---------- GET object ----------

    private static async Task GetItem(HttpContext ctx, Caller caller, Guid listId, string uid)
    {
        var ct = ctx.RequestAborted;
        var access = ctx.RequestServices.GetRequiredService<AccessResolver>();
        var acc = await access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct);
        if (!acc.Allowed) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var dav = ctx.RequestServices.GetRequiredService<TaskDavService>();
        var item = await dav.FindAsync(listId, uid, ct);
        if (item is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        ctx.Response.Headers.ETag = Etag(item);
        ctx.Response.ContentType = "text/calendar; charset=utf-8";
        if (ctx.Request.Method == "HEAD") return;
        await ctx.Response.WriteAsync(VtodoMapper.ToVtodo(item, acc.List!.Tags, item.SourceVtodo), ct);
    }

    // ---------- write path: object PUT / DELETE with ETag preconditions ----------

    private static async Task HandlePut(HttpContext ctx, Caller caller, Guid listId, string uid)
    {
        var raw = await ReadBody(ctx);
        var (ifMatch, ifNoneMatchStar) = Preconditions(ctx);
        var result = await ctx.RequestServices.GetRequiredService<TaskDavService>()
            .PutVtodoAsync(caller, listId, uid, raw, ifMatch, ifNoneMatchStar, ctx.RequestAborted);
        if (result.Status == OpStatus.Ok && result.Value is { } w)
        {
            ctx.Response.Headers.ETag = $"\"{w.Etag}\"";
            ctx.Response.StatusCode = w.Created ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
            return;
        }
        ctx.Response.StatusCode = DavProtocol.DavStatus(result.Status);
    }

    private static async Task HandleDelete(HttpContext ctx, Caller caller, Guid listId, string uid)
    {
        var (ifMatch, _) = Preconditions(ctx);
        var result = await ctx.RequestServices.GetRequiredService<TaskDavService>()
            .DeleteByUidAsync(caller, listId, uid, ifMatch, ctx.RequestAborted);
        ctx.Response.StatusCode = DavProtocol.DavStatus(result.Status);
    }

    private static (string? IfMatch, bool IfNoneMatchStar) Preconditions(HttpContext ctx)
    {
        var ifMatch = ctx.Request.Headers.TryGetValue("If-Match", out var im) && im.Count > 0 ? im.ToString() : null;
        var ifNoneMatch = ctx.Request.Headers.TryGetValue("If-None-Match", out var n) ? n.ToString() : null;
        return DavProtocol.ParsePreconditions(ifMatch, ifNoneMatch);
    }

    // ---------- helpers ----------

    private static string Seg(string value) => Uri.EscapeDataString(value);

    private static string Etag(Item i) => $"\"{i.Version}\"";

    private static void WriteOptions(HttpContext ctx)
    {
        ctx.Response.Headers["DAV"] = "1, 2, 3, calendar-access";
        ctx.Response.Headers["Allow"] = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, REPORT";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task WriteMultiStatus(HttpContext ctx, XElement multistatus)
    {
        ctx.Response.StatusCode = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), multistatus);
        await ctx.Response.WriteAsync(doc.Declaration + "\n" + doc.ToString(SaveOptions.DisableFormatting), ctx.RequestAborted);
    }

    private static XElement MultiStatus(params XElement[] responses) => new(D + "multistatus",
        new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "cs", CS.NamespaceName),
        responses);

    private static XElement MultiStatusWithToken(long token, XElement[] responses)
    {
        var ms = MultiStatus(responses);
        ms.Add(new XElement(D + "sync-token", token.ToString(CultureInfo.InvariantCulture)));
        return ms;
    }

    private static XElement DeletedResponse(string href) => new(D + "response",
        new XElement(D + "href", href),
        new XElement(D + "status", "HTTP/1.1 404 Not Found"));

    private static XElement SupportedReports(params XName[] reports) => new(D + "supported-report-set",
        reports.Select(r => new XElement(D + "supported-report", new XElement(D + "report", new XElement(r)))));

    private static XElement Response(string href, params XElement[] props) => new(D + "response",
        new XElement(D + "href", href),
        new XElement(D + "propstat",
            new XElement(D + "prop", props.Cast<object>().ToArray()),
            new XElement(D + "status", "HTTP/1.1 200 OK")));

    private static XElement Href(string url) => new(D + "href", url);

    private static async Task<string> ReadBody(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync(ctx.RequestAborted);
    }
}
