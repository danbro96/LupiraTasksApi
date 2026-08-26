namespace LupiraTasksApi.Dav;

public sealed class DavResourceDto
{
    public required string Uid { get; set; }
    public required string Etag { get; set; }
    public string? Content { get; set; }
}
