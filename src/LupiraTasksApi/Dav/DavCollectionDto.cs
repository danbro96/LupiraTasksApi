namespace LupiraTasksApi.Dav;

public sealed class DavCollectionDto
{
    public required Guid Id { get; set; }

    public required DavCollectionKind Kind { get; set; }

    public string? DisplayName { get; set; }

    public required string Ctag { get; set; }

    public required string SyncToken { get; set; }
}
