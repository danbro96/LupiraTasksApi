namespace LupiraTasksApi.Dav;

/// <summary>One endpoint covers Depth:1 listing (all null), multiget (uids + includeContent), and
/// calendar-query time-range (start/end; not applicable to address books).</summary>
public sealed class DavQueryRequest
{
    public List<string>? Uids { get; set; }

    public DateTimeOffset? Start { get; set; }

    public DateTimeOffset? End { get; set; }

    public bool IncludeContent { get; set; }
}
