using JasperFx.Events;

namespace LupiraTasksApi.Domain.Shares;

/// <summary>
/// Inline Marten snapshot for the <c>ShareLink</c> stream (stream id = <see cref="Id"/>, a
/// non-secret GUID). A share link grants account-less access to ONE list at one
/// <see cref="ShareAccess"/> level until <see cref="Revoked"/> or past <see cref="ExpiresAt"/>.
/// The <see cref="Token"/> is the opaque secret presented by a recipient (looked up via its
/// unique index); attribution fields are read from the <c>actor</c> event header.
/// </summary>
public sealed class ShareLink
{
    public Guid Id { get; set; }
    public int Version { get; set; }

    public Guid ListId { get; set; }
    public string Token { get; set; } = "";
    public ShareAccess Access { get; set; }
    public string Label { get; set; } = "";

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }

    /// <summary>True when the link can currently be used (not revoked, not past expiry).</summary>
    public bool IsActive(DateTimeOffset now) => !Revoked && (ExpiresAt is null || ExpiresAt > now);

    public void Apply(IEvent<ShareLinkCreated> e)
    {
        var data = e.Data;
        Id = data.ShareId;
        ListId = data.ListId;
        Token = data.Token;
        Access = data.Access;
        Label = data.Label;
        ExpiresAt = data.ExpiresAt;
        CreatedBy = EventActor.Of(e);
        CreatedAt = e.Timestamp;
    }

    public void Apply(IEvent<ShareLinkRevoked> e)
    {
        Revoked = true;
        RevokedAt = e.Timestamp;
        RevokedBy = EventActor.Of(e);
    }
}
