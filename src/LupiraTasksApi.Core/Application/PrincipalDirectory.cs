using LupiraTasksApi.Domain.Identity;
using Marten;

namespace LupiraTasksApi.Application;

/// <summary>
/// Resolves an authenticated login (OIDC <c>sub</c> + email, or a DAV email) to a local
/// <see cref="Principal"/>, JIT-provisioning on first sight. Resolves by <c>sub</c> first then email so
/// the OIDC and DAV logins converge on one row; a DAV-first row gets an <c>email|{email}</c> placeholder
/// sub that is upgraded when the real OIDC <c>sub</c> later appears. The single funnel from a login to an
/// internal principal id; the batch <see cref="LookupAsync"/> resolves stored ids back to people on reads.
/// Mirrors LupiraCalApi's PrincipalDirectory (the platform identity pattern).
/// </summary>
public sealed class PrincipalDirectory
{
    private readonly IDocumentSession _session;

    public PrincipalDirectory(IDocumentSession session)
    {
        _session = session;
    }

    /// <summary>Look up an existing principal by login email without provisioning — for surfaces where a
    /// missing principal is "not found" (role change/remove, assignee filter), not a reason to create one.</summary>
    public async Task<Principal?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        return email.Length == 0 ? null : await _session.Query<Principal>().FirstOrDefaultAsync(x => x.Email == email, ct);
    }

    /// <summary>Resolve a login to its <see cref="Principal"/>, provisioning on first sight. Saves only when
    /// something changed (new row, sub upgrade, or refreshed email/name), so steady-state reads don't write.</summary>
    public async Task<Principal> ResolveOrProvisionAsync(string? sub, string email, string? name, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();

        Principal? p = null;
        if (sub is not null) p = await _session.Query<Principal>().FirstOrDefaultAsync(x => x.AuthentikSub == sub, ct);
        if (p is null && email.Length > 0) p = await _session.Query<Principal>().FirstOrDefaultAsync(x => x.Email == email, ct);

        var now = DateTimeOffset.UtcNow;
        if (p is null)
        {
            p = new Principal
            {
                Id = Guid.CreateVersion7(),
                AuthentikSub = sub ?? $"email|{email}",
                Email = email,
                DisplayName = name,
                CreatedAt = now,
                LastSeenAt = now,
            };
            _session.Store(p);
            await _session.SaveChangesAsync(ct);
            return p;
        }

        var changed = false;
        // Upgrade a DAV-first placeholder sub to the real one the first time an OIDC login shows up.
        if (sub is not null && p.AuthentikSub != sub && p.AuthentikSub.StartsWith("email|", StringComparison.Ordinal))
        {
            p.AuthentikSub = sub;
            changed = true;
        }
        if (email.Length > 0 && p.Email != email) { p.Email = email; changed = true; }
        if (name is not null && p.DisplayName != name) { p.DisplayName = name; changed = true; }
        if (p.LastSeenAt != now) { p.LastSeenAt = now; changed = true; }
        if (changed) { _session.Store(p); await _session.SaveChangesAsync(ct); }
        return p;
    }

    /// <summary>Batch-resolve stored principal ids to their <see cref="Principal"/> rows for the read
    /// boundary (owner/members/assignee/attribution → <c>PersonRef</c>).</summary>
    public async Task<IReadOnlyDictionary<Guid, Principal>> LookupAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var distinct = ids.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (distinct.Length == 0) return EmptyLookup;
        var rows = await _session.Query<Principal>().Where(p => distinct.Contains(p.Id)).ToListAsync(ct);
        return rows.ToDictionary(p => p.Id);
    }

    private static readonly IReadOnlyDictionary<Guid, Principal> EmptyLookup = new Dictionary<Guid, Principal>();
}
