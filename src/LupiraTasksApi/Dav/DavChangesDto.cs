namespace LupiraTasksApi.Dav;

public sealed class DavChangesDto
{
    public required string SyncToken { get; set; }

    public required List<DavChangeDto> Changed { get; set; }

    public required List<string> Deleted { get; set; }
}
