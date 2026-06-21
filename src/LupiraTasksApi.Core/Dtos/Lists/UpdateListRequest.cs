namespace LupiraTasksApi.Dtos.Lists;

/// <summary>
/// Patch a list's metadata. Each non-null field emits its own event; omit a field
/// (leave it <c>null</c>) to leave it unchanged. To clear the color, send
/// <see cref="ColorProvided"/> = <c>true</c> with <see cref="Color"/> = <c>null</c>.
/// </summary>
public sealed class UpdateListRequest
{
    public string? Name { get; set; }

    /// <summary>The new color. Honored only when <see cref="ColorProvided"/> is true.</summary>
    public string? Color { get; set; }

    /// <summary>Set true to apply <see cref="Color"/> (including clearing it to null).</summary>
    public bool ColorProvided { get; set; }

    /// <summary>Whether priority is a simple on/off (true) or the full 0..9 scale (false). Null leaves it unchanged.</summary>
    public bool? SimplePriority { get; set; }
}
