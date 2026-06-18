using System.Globalization;
using System.Xml.Linq;
using LupiraTasksApi.Application;

namespace LupiraTasksApi.Dav;

/// <summary>
/// Pure (no HttpContext, no Marten) WebDAV protocol helpers behind <see cref="DavRouter"/>:
/// request-body parsing, ETag-precondition parsing, and status mapping. Split out so the fiddly
/// edge cases (malformed tokens, hostile XML, missing separators) stay unit-testable without the
/// full HTTP + DB stack. Adapted from LupiraCalApi's <c>DavProtocol</c> — the calendar-only
/// time-range/recurrence helpers are dropped (tasks have no time-range queries in v1).
/// </summary>
internal static class DavProtocol
{
    private static readonly XNamespace D = "DAV:";

    public static XDocument? TryParseXml(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return XDocument.Parse(body); } catch { return null; }
    }

    public static long? ParseSyncToken(XDocument doc)
    {
        var el = doc.Descendants(D + "sync-token").FirstOrDefault();
        var v = el?.Value.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    /// <summary>The bare resource UIDs referenced by <c>&lt;D:href&gt;</c> elements (a calendar-multiget),
    /// with the given extension stripped. Empty when the body is a query rather than a multiget.</summary>
    public static List<string> ExtractHrefUids(string body, string ext)
    {
        var uids = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(body)) return [];
        try
        {
            var doc = XDocument.Parse(body);
            foreach (var href in doc.Descendants(D + "href"))
            {
                var name = href.Value.TrimEnd('/');
                var slash = name.LastIndexOf('/');
                if (slash >= 0) name = name[(slash + 1)..];
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
                if (name.Length > 0) uids.Add(name);
            }
        }
        catch { /* malformed → treat as query (return all) */ }
        return [.. uids];
    }

    public static string StripExt(string file)
    {
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    /// <summary>Parses the raw <c>If-Match</c>/<c>If-None-Match</c> headers. An <c>If-Match</c> of <c>*</c>
    /// (or empty) means "no specific tag"; quotes are stripped from a concrete tag. Returns whether
    /// <c>If-None-Match</c> is the <c>*</c> wildcard (the "create only if absent" guard).</summary>
    public static (string? IfMatch, bool IfNoneMatchStar) ParsePreconditions(string? ifMatchHeader, string? ifNoneMatchHeader)
    {
        string? ifMatch = null;
        var im = ifMatchHeader?.Trim();
        if (!string.IsNullOrEmpty(im) && im != "*") ifMatch = im.Trim('"');
        var inm = ifNoneMatchHeader?.Trim() ?? "";
        return (ifMatch, inm == "*");
    }

    public static int DavStatus(OpStatus status) => status switch
    {
        OpStatus.Ok => StatusCodes.Status204NoContent,
        OpStatus.Forbidden => StatusCodes.Status403Forbidden,
        OpStatus.NotFound => StatusCodes.Status404NotFound,
        OpStatus.Conflict => StatusCodes.Status412PreconditionFailed,
        OpStatus.Invalid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };
}
