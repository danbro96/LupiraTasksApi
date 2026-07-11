using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Users;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// People discovery for member-add: the distinct principals the caller has seen across their own shared
/// lists, resolved to email/display name. Optionally filtered by a query over email or display name.
/// </summary>
public sealed class UsersHandler
{
    private readonly IQuerySession _session;
    private readonly CurrentUser _user;
    private readonly PrincipalDirectory _directory;

    public UsersHandler(IQuerySession session, CurrentUser user, PrincipalDirectory directory)
    {
        _session = session;
        _user = user;
        _directory = directory;
    }

    public async Task<Results<Ok<DirectoryResponse>, UnauthorizedHttpResult>> DirectoryAsync(
        string? q,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var me = await _directory.ResolveOrProvisionAsync(_user.Sub, email, _user.DisplayName, ct);

        var lists = await _session.Query<TodoList>()
            .Where(l => !l.IsDeleted && l.Members.Any(m => m.PrincipalId == me.Id))
            .ToListAsync(ct);

        var ids = lists
            .SelectMany(l => l.Members.Select(m => m.PrincipalId))
            .Where(id => id != me.Id)
            .Distinct();

        var principals = await _directory.LookupAsync(ids, ct);

        var needle = q?.Trim();
        var people = principals.Values
            .Where(p => string.IsNullOrEmpty(needle)
                || p.Email.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (p.DisplayName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(p => p.Email, StringComparer.OrdinalIgnoreCase)
            .Select(p => new DirectoryPerson { PrincipalId = p.Id, Email = p.Email, DisplayName = p.DisplayName })
            .ToList();

        return TypedResults.Ok(new DirectoryResponse { People = people });
    }
}
