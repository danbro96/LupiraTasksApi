using System.Text.Json.Nodes;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Relations;

namespace LupiraTasksApi.Mappers;

/// <summary>Maps the <see cref="Relation"/> document to its response DTO.</summary>
internal static class RelationMapper
{
    public static RelationDto ToResponse(this Relation r) => new()
    {
        Id = r.Id,
        FromKind = r.FromKind,
        FromId = r.FromId,
        ToKind = r.ToKind,
        ToRef = r.ToRef,
        RelationType = r.RelationType,
        Metadata = string.IsNullOrWhiteSpace(r.Metadata) ? null : JsonNode.Parse(r.Metadata),
    };
}
