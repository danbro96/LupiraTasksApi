using System.Security.Cryptography;
using JasperFx;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Shares;
using LupiraTasksApi.Mappers;
using LupiraTasksApi.Dtos.Shared;
using LupiraTasksApi.Dtos.Shares;
using LupiraTasksApi.Data;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.Options;

namespace LupiraTasksApi.Application;

/// <summary>
/// Owner-side management of share links over the <c>ShareLink</c> event stream: mint (read or
/// read/write, optional expiry), list, and revoke. All three require the caller to be an
/// <see cref="ListRole.Owner"/> of the target list. The public consumption side (resolving a token
/// to a grant, the trimmed read view, and read/write item mutations) lives behind the
/// <c>/shared/{token}</c> surface and reuses <see cref="ItemService"/>.
/// </summary>
public sealed class ShareService
{
    private const int MaxLabelLength = 80;

    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;
    private readonly Idempotency _idempotency;
    private readonly string _linkBaseUrl;

    public ShareService(IDocumentSession session, AccessResolver access, Idempotency idempotency, IOptions<ShareLinkOptions> options)
    {
        _session = session;
        _access = access;
        _idempotency = idempotency;
        _linkBaseUrl = options.Value.LinkBaseUrl.TrimEnd('/');
    }

    public async Task<OpResult<ShareResponse>> CreateAsync(Caller caller, Guid? cmdId, Guid listId, CreateShareRequest request, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Owner, ct);
        if (!access.Allowed) return OpResult<ShareResponse>.NotFound();

        if (request.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
            return OpResult<ShareResponse>.Invalid("`expiresAt` must be in the future.");

        var label = string.IsNullOrWhiteSpace(request.Label) ? DefaultLabel(request.Access) : request.Label.Trim();
        if (label.Length > MaxLabelLength)
            return OpResult<ShareResponse>.Invalid($"`label` must be at most {MaxLabelLength} characters.");

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            var prior = await _session.LoadAsync<ShareLink>(seen.AggregateId, ct);
            if (prior is not null) return OpResult<ShareResponse>.Ok(ToResponse(prior));
        }

        var shareId = Guid.CreateVersion7();
        var token = NewToken();

        _session.SetHeader(EventActor.HeaderKey, caller.Actor);
        try
        {
            _session.Events.StartStream<ShareLink>(
                shareId, new ShareLinkCreated(shareId, listId, token, request.Access, label, request.ExpiresAt));
            _idempotency.Record(commandId, shareId, version: 1);
            await _session.SaveChangesAsync(ct);
        }
        catch (ExistingStreamIdCollisionException)
        {
            // Idempotent — the (server-minted) stream already exists.
        }
        catch (DocumentAlreadyExistsException)
        {
            var prior = await ReResolveAsync(commandId, shareId, ct);
            if (prior is not null) return OpResult<ShareResponse>.Ok(ToResponse(prior));
        }

        var link = await _session.LoadAsync<ShareLink>(shareId, ct);
        return link is null
            ? OpResult<ShareResponse>.Invalid("Share link could not be created.")
            : OpResult<ShareResponse>.Ok(ToResponse(link));
    }

    public async Task<OpResult<ShareCollectionResponse>> ListAsync(Caller caller, Guid listId, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Owner, ct);
        if (!access.Allowed) return OpResult<ShareCollectionResponse>.NotFound();

        var links = await _session.Query<ShareLink>()
            .Where(s => s.ListId == listId && !s.Revoked)
            .ToListAsync(ct);

        var shares = links
            .OrderByDescending(s => s.CreatedAt)
            .Select(ToResponse)
            .ToList();

        return OpResult<ShareCollectionResponse>.Ok(new ShareCollectionResponse { Shares = shares });
    }

    public async Task<OpResult> RevokeAsync(Caller caller, Guid? cmdId, Guid listId, Guid shareId, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Owner, ct);
        if (!access.Allowed) return OpResult.NotFound();

        var link = await _session.LoadAsync<ShareLink>(shareId, ct);
        if (link is null || link.ListId != listId) return OpResult.NotFound();
        if (link.Revoked) return OpResult.Ok(); // already revoked — idempotent

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult.Ok();

        _session.SetHeader(EventActor.HeaderKey, caller.Actor);
        await _idempotency.AppendDedupAsync(
            commandId, shareId, new object[] { new ShareLinkRevoked(shareId, "Revoked by owner") }, ct);

        return OpResult.Ok();
    }

    /// <summary>
    /// The public, account-less read of a shared list: authorize the share grant (Viewer), load the
    /// list + live items, and map to the TRIMMED <see cref="SharedListResponse"/> (no emails). Used by
    /// the <c>/shared/{token}</c> surface; the caller must be a share caller.
    /// </summary>
    public async Task<OpResult<SharedListResponse>> GetSharedListAsync(Caller caller, CancellationToken ct)
    {
        if (caller.Share is not { } grant) return OpResult<SharedListResponse>.NotFound();

        var access = await _access.AuthorizeAsync(caller, grant.ListId, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<SharedListResponse>.NotFound();

        var items = await _session.Query<Item>()
            .Where(i => i.ListId == grant.ListId && !i.Deleted)
            .ToListAsync(ct);

        var ordered = items
            .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
            .Select(i => i.ToShared())
            .ToList();

        return OpResult<SharedListResponse>.Ok(access.List!.ToShared(grant.Access, ordered));
    }

    private async Task<ShareLink?> ReResolveAsync(Guid commandId, Guid shareId, CancellationToken ct)
    {
        var seen = await _idempotency.SeenAsync(commandId, ct);
        return await _session.LoadAsync<ShareLink>(seen?.AggregateId ?? shareId, ct);
    }

    private ShareResponse ToResponse(ShareLink s) => new()
    {
        ShareId = s.Id,
        Token = s.Token,
        Url = $"{_linkBaseUrl}/s/{s.Token}",
        Access = s.Access,
        Label = s.Label,
        CreatedAt = s.CreatedAt,
        ExpiresAt = s.ExpiresAt,
        Revoked = s.Revoked,
    };

    private static string DefaultLabel(ShareAccess access) =>
        access == ShareAccess.ReadWrite ? "Shared link (read/write)" : "Shared link (read-only)";

    /// <summary>A 256-bit URL-safe opaque token.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
