using System.Text.Json.Serialization;

namespace LupiraTasksApi.Dav;

// The wire shapes of the internal /dav-backend contract (docs/dav-backend-contract.md), implemented
// identically by lupira-cal-api, lupira-tasks-api, and lupira-contact-api and consumed by the
// LupiraDavApi gateway. ETags are unquoted in JSON bodies; sync tokens are opaque strings the
// gateway never parses.

[JsonConverter(typeof(JsonStringEnumConverter<DavCollectionKind>))]
public enum DavCollectionKind { EventCalendar, TodoList, AddressBook }

public sealed class DavPrincipalDto
{
    public string? DisplayName { get; set; }
}

public sealed class DavCollectionDto
{
    public required Guid Id { get; set; }
    public required DavCollectionKind Kind { get; set; }
    public string? DisplayName { get; set; }
    public required string Ctag { get; set; }
    public required string SyncToken { get; set; }
}

public sealed class DavCollectionsDto
{
    public required DavPrincipalDto Principal { get; set; }
    public required List<DavCollectionDto> Collections { get; set; }
}

/// <summary>One endpoint covers Depth:1 listing (all null), multiget (uids + includeContent), and
/// calendar-query time-range (start/end; not applicable to address books).</summary>
public sealed class DavQueryRequest
{
    public List<string>? Uids { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public bool IncludeContent { get; set; }
}

public sealed class DavResourceDto
{
    public required string Uid { get; set; }
    public required string Etag { get; set; }
    public string? Content { get; set; }
}

public sealed class DavResourcesDto
{
    public required List<DavResourceDto> Resources { get; set; }
}

public sealed class DavChangeDto
{
    public required string Uid { get; set; }
    public required string Etag { get; set; }
}

public sealed class DavChangesDto
{
    public required string SyncToken { get; set; }
    public required List<DavChangeDto> Changed { get; set; }
    public required List<string> Deleted { get; set; }
}
