using System.Text.Json.Nodes;

namespace LupiraTasksApi.Dtos.Relations;

public sealed class CreateRelationRequest
{
    public required string ToKind { get; set; }
    public required string ToRef { get; set; }
    public required string RelationType { get; set; }
    public JsonNode? Metadata { get; set; }
}
