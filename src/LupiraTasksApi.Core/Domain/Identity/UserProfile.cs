namespace LupiraTasksApi.Domain.Identity;

/// <summary>
/// Plain Marten document (not event-sourced) caching a user's identity. Keyed by
/// email (= OIDC subject). Upserted on the first <c>/me</c> call and refreshed on
/// each call; used to resolve display names in the membership directory and on
/// attribution. A wrong/never-seen email is simply absent here.
/// </summary>
public sealed class UserProfile
{
    /// <summary>Marten document identity — the user's email (OIDC subject).</summary>
    public string Id { get; set; } = "";

    public string? DisplayName { get; set; }

    public bool IsAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
