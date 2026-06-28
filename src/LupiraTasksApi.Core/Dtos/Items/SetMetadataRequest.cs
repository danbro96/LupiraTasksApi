using System.Text.Json.Nodes;

namespace LupiraTasksApi.Dtos.Items;

/// <summary>Sets an item's free-form JSON metadata (whole-field). Send <c>null</c> to clear it.</summary>
public sealed class SetMetadataRequest
{
    public JsonNode? Metadata { get; set; }

    /// <summary>Client wall-clock for LWW; defaults to server now when omitted.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}
