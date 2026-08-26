namespace LupiraTasksApi.Core.Dtos.Ping;

/// <summary>The claims a caller's token resolved to, echoed for cross-service auth-seam probes.
/// Returning the audiences (not a bare 200) lets a failing consumer see which half of the seam
/// is misconfigured.</summary>
public sealed class PingDto
{
    public required string Subject { get; set; }

    public required IReadOnlyList<string> Audiences { get; set; }

    public string? Email { get; set; }
}
