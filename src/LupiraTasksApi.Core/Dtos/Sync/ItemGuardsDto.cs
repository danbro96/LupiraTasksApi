using Lupira.Sync;

namespace LupiraTasksApi.Core.Dtos.Sync;

/// <summary>The write that owns each field's current value, so an offline client resolves its pending edits
/// field by field against the server state. <c>tags</c> is keyed by tag id and keeps removed tags.</summary>
public sealed class ItemGuardsDto
{
    public required SectionGuardDto Name { get; set; }

    public required SectionGuardDto Notes { get; set; }

    public required SectionGuardDto Assignee { get; set; }

    public required SectionGuardDto Due { get; set; }

    public required SectionGuardDto Qty { get; set; }

    public required SectionGuardDto Priority { get; set; }

    public required SectionGuardDto Status { get; set; }

    public required SectionGuardDto Move { get; set; }

    public required SectionGuardDto Metadata { get; set; }

    public required Dictionary<Guid, SectionGuardDto> Tags { get; set; }
}
