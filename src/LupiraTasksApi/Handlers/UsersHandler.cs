using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Users;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// People discovery for member-add: the distinct members the caller has seen across their own
/// shared lists, resolved to <c>UserProfile</c> display names. Optionally filtered by a query.
/// </summary>
public sealed class UsersHandler
{
    private readonly IDocumentSession _session;
    private readonly CurrentUser _user;

    public UsersHandler(IDocumentSession session, CurrentUser user)
    {
        _session = session;
        _user = user;
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

        var lists = await _session.Query<TodoList>()
            .Where(l => !l.IsDeleted && l.Members.Any(m => m.Email == email))
            .ToListAsync(ct);

        var emails = lists
            .SelectMany(l => l.Members.Select(m => m.Email))
            .Where(e => !string.Equals(e, email, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var needle = q?.Trim();
        if (!string.IsNullOrEmpty(needle))
        {
            emails = emails.Where(e => e.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var names = emails.Count == 0
            ? new Dictionary<string, string?>()
            : (await _session.Query<UserProfile>().Where(p => emails.Contains(p.Id)).ToListAsync(ct))
                .ToDictionary(p => p.Id, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);

        var people = emails
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .Select(e => new DirectoryPerson { Email = e, DisplayName = names.GetValueOrDefault(e) })
            .ToList();

        return TypedResults.Ok(new DirectoryResponse { People = people });
    }
}
