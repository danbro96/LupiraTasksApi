using LupiraTasksApi.Domain.Identity;

namespace LupiraTasksApi.Dtos;

/// <summary>
/// The canonical identity projection on the read boundary: the stable <see cref="PrincipalId"/> (the
/// key a client should store/compare) plus the current <see cref="Email"/> and <see cref="DisplayName"/>
/// for display. Every identity slot the API returns (owner, member, assignee, creator, completer,
/// directory person) is a <c>PersonRef</c>; identity <em>inputs</em> stay email (invite/assign). Built
/// from a resolved <see cref="Principal"/>.
/// </summary>
public sealed class PersonRef
{
    public required Guid PrincipalId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }

    public static PersonRef From(Principal p) => new()
    {
        PrincipalId = p.Id,
        Email = p.Email,
        DisplayName = p.DisplayName,
    };

    /// <summary>Resolve a stored <c>PrincipalId</c> to a <c>PersonRef</c> via a lookup dict; <c>null</c>
    /// when the id is unknown (defensive — provisioning makes this unlikely).</summary>
    public static PersonRef? From(Guid principalId, IReadOnlyDictionary<Guid, Principal> lookup) =>
        lookup.TryGetValue(principalId, out var p) ? From(p) : null;

    /// <summary>Resolve an <c>actor</c> value (a principal id string, or a <c>share:{label}</c> sentinel)
    /// to a <c>PersonRef</c>; <c>null</c> for a share actor or an unknown id — attribution by a share link
    /// is not a person.</summary>
    public static PersonRef? FromActor(string? actor, IReadOnlyDictionary<Guid, Principal> lookup) =>
        Guid.TryParse(actor, out var id) ? From(id, lookup) : null;
}
