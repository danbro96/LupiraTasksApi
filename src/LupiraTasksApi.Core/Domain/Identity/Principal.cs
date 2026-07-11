namespace LupiraTasksApi.Domain.Identity;

/// <summary>
/// A user identity (plain Marten document, JIT-provisioned from Authentik). <see cref="AuthentikSub"/>
/// is the durable anchor; <see cref="Email"/> is the mutable OIDC join key (also the acting-user key on
/// the <c>/dav-backend</c> seam). Everything internal — membership, ownership, assignment, and event
/// attribution — references the immutable <see cref="Id"/> (a Guid), so an email change never strands
/// access and no email is baked into event payloads. Resolved/provisioned via <c>PrincipalDirectory</c>.
/// </summary>
public sealed class Principal
{
    /// <summary>Marten document identity — the stable internal principal id used everywhere internally.</summary>
    public Guid Id { get; set; }

    /// <summary>The immutable Authentik subject. A DAV-first (email-only) login gets an <c>email|{email}</c>
    /// placeholder, upgraded to the real <c>sub</c> when the OIDC login first arrives.</summary>
    public string AuthentikSub { get; set; } = "";

    /// <summary>The current login email (mutable). Indexed; the join key for OIDC/DAV logins and invites.</summary>
    public string Email { get; set; } = "";

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
