using System.Text.Json.Nodes;

namespace LupiraTasksApi.Dtos.Relations;

public sealed class RelationDto
{
    public required Guid Id { get; set; }
    public required string FromKind { get; set; }
    public required Guid FromId { get; set; }
    public required string ToKind { get; set; }
    public required string ToRef { get; set; }
    public required string RelationType { get; set; }
    public JsonNode? Metadata { get; set; }
}
