using System.Globalization;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using IcalCalendar = Ical.Net.Calendar;

namespace LupiraTasksApi.Ical;

/// <summary>The modeled fields lifted out of an inbound VTODO. Everything else (PRIORITY, RRULE, X-*…)
/// is preserved opaquely in the raw blob and re-emitted by <see cref="VtodoMapper.ToVtodo"/>.</summary>
public readonly record struct VtodoFields(
    string Title,
    string? Notes,
    DateTimeOffset? DueAt,
    bool Completed,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> Categories,
    int Priority);

/// <summary>
/// Maps <see cref="Item"/> ↔ iCalendar VTODO (RFC 5545) via Ical.Net, for the CalDAV surface.
///
/// GET <b>regenerates</b> the VTODO from the live snapshot rather than echoing a stored blob: the
/// REST/MCP surfaces edit fields with granular events that never touch <c>SourceVtodo</c>, so a
/// stored blob would go stale. To stay lossless, the modeled properties are written from the
/// snapshot and every <em>unmodeled</em> top-level property from the last DAV PUT
/// (<see cref="Item.SourceVtodo"/>) is spliced back in. (VALARM sub-components are stored in the
/// raw blob but not re-emitted in v1 — a known, documented gap.)
/// </summary>
public static class VtodoMapper
{
    // Properties this model owns/sets explicitly; everything else from the source blob is preserved.
    private static readonly HashSet<string> ModeledProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "UID", "SUMMARY", "DESCRIPTION", "DUE", "STATUS", "PERCENT-COMPLETE", "COMPLETED",
        "CATEGORIES", "PRIORITY", "DTSTAMP", "CREATED", "LAST-MODIFIED",
        "X-LUPIRA-ASSIGNEE", "X-LUPIRA-QUANTITY", "X-LUPIRA-UNIT",
    };

    public static string ToVtodo(Item item, IReadOnlyList<TagDef> listTags, string? sourceRaw)
    {
        var todo = new Todo { Uid = item.Uid };

        if (!string.IsNullOrWhiteSpace(item.Title)) todo.Summary = item.Title;
        if (!string.IsNullOrWhiteSpace(item.Notes)) todo.Description = item.Notes;
        if (item.DueAt is { } due) todo.Due = Utc(due);
        // Standard VTODO PRIORITY (RFC 5545): 1..9 in range; 0 = undefined, so omit it.
        if (item.Priority is >= 1 and <= 9) todo.Priority = item.Priority;

        if (item.Completed)
        {
            todo.Status = "COMPLETED";
            todo.PercentComplete = 100;
            todo.Completed = Utc(item.CompletedAt ?? item.UpdatedAt);
        }
        else
        {
            todo.Status = "NEEDS-ACTION";
            todo.PercentComplete = 0;
        }

        todo.Created = Utc(item.CreatedAt);
        todo.LastModified = Utc(item.UpdatedAt);
        todo.DtStamp = Utc(item.UpdatedAt);

        foreach (var tagId in item.Tags)
        {
            var def = listTags.FirstOrDefault(t => t.Id == tagId);
            if (def is not null && !string.IsNullOrWhiteSpace(def.Label)) todo.Categories.Add(def.Label);
        }

        // Non-standard fields ride as X-props (round-tripped from the snapshot, not the blob).
        if (!string.IsNullOrWhiteSpace(item.AssignedTo))
            todo.Properties.Add(new CalendarProperty("X-LUPIRA-ASSIGNEE", item.AssignedTo));
        if (item.Quantity is { } q)
            todo.Properties.Add(new CalendarProperty("X-LUPIRA-QUANTITY", q.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(item.Unit))
            todo.Properties.Add(new CalendarProperty("X-LUPIRA-UNIT", item.Unit));

        // Preserve unmodeled properties (PRIORITY, RRULE, X-*…) from the last DAV PUT.
        if (TryLoadTodo(sourceRaw, out var src) && src is not null)
            foreach (var p in src.Properties)
                if (!ModeledProps.Contains(p.Name) && p.Value is { } value)
                    todo.Properties.Add(new CalendarProperty(p.Name, value));

        var cal = new IcalCalendar();
        cal.Todos.Add(todo);
        return new CalendarSerializer().SerializeToString(cal) ?? string.Empty;
    }

    /// <summary>Parses the modeled fields from an inbound VTODO. Throws <see cref="FormatException"/>
    /// on payloads Ical.Net can't read or that contain no VTODO.</summary>
    public static VtodoFields Parse(string raw)
    {
        if (!TryLoadTodo(raw, out var todo) || todo is null)
            throw new FormatException("No VTODO in payload.");

        var completed =
            string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)
            || todo.PercentComplete >= 100
            || todo.Completed is not null;

        DateTimeOffset? completedAt = todo.Completed is { } c ? new DateTimeOffset(c.AsUtc, TimeSpan.Zero) : null;
        DateTimeOffset? dueAt = todo.Due is { } d ? new DateTimeOffset(d.AsUtc, TimeSpan.Zero) : null;
        var categories = (todo.Categories ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        // PRIORITY is defined 0..9; clamp defensively so a stray client value can't fail the sync.
        var priority = Math.Clamp(todo.Priority, 0, 9);

        return new VtodoFields(todo.Summary ?? "", todo.Description, dueAt, completed, completedAt, categories, priority);
    }

    private static CalDateTime Utc(DateTimeOffset value) => new(value.UtcDateTime, "UTC");

    private static bool TryLoadTodo(string? raw, out Todo? todo)
    {
        todo = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            var cal = IcalCalendar.Load(raw);
            todo = cal?.Todos.FirstOrDefault();
            return todo is not null;
        }
        catch
        {
            return false;
        }
    }
}
