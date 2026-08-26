namespace LupiraTasksApi.Dav;

public sealed class DavCollectionsDto
{
    public required DavPrincipalDto Principal { get; set; }

    public required List<DavCollectionDto> Collections { get; set; }
}
