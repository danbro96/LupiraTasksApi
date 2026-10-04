using Lupira.Identity.Marten;
using LupiraTasksApi.Core.Application;
using LupiraTasksApi.Core.Auth;
using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Domain.Items.Events;
using LupiraTasksApi.Core.Domain.Lists;
using LupiraTasksApi.Core.Domain.Lists.Events;
using Marten;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// The two payoffs of anchoring identity on the internal principal id rather than the mutable email:
/// a login email can change without stranding access, and event streams carry only guids (no PII),
/// so deleting the identity document is a clean crypto-shred.
/// </summary>
public sealed class PrincipalIdentityTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    /// <summary>
    /// Concurrent first-sight logins race through the check-then-insert in
    /// <c>ResolveOrProvisionAsync</c>. The unique index on <c>AuthentikSub</c> lets one win and the losers
    /// adopt its row, so one login can never fork into two principals — which previously stranded
    /// membership behind whichever duplicate Postgres happened to return.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_logins_converge_on_one_principal()
    {
        const string sub = "authentik-sub-concurrent-provision";

        // Each task needs its own session — IDocumentSession is not thread-safe, so sharing one
        // would serialize the writes and never exercise the race.
        var resolved = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var s = Store.LightweightSession();
            return (await new PrincipalDirectory(s).ResolveOrProvisionAsync(sub, "racer@x.test", "Racer")).Id;
        }));

        Assert.Single(resolved.Distinct());

        await using var q = Store.QuerySession();
        var rows = await q.Query<Principal>().Where(x => x.AuthentikSub == sub).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(rows[0].Id, resolved[0]);
    }

    /// <summary>Repeated resolution of the same login returns a constant id — the property that broke when
    /// an unordered <c>FirstOrDefault</c> ran over duplicate rows.</summary>
    [Fact]
    public async Task Repeated_resolution_is_stable()
    {
        await using var s = Store.LightweightSession();
        var directory = new PrincipalDirectory(s);

        var first = await directory.ResolveOrProvisionAsync("authentik-sub-stable", "stable@x.test", "Stable");
        for (var i = 0; i < 10; i++)
            Assert.Equal(first.Id, (await directory.ResolveOrProvisionAsync("authentik-sub-stable", "stable@x.test", "Stable")).Id);
    }

    [Fact]
    public async Task Email_change_keeps_the_same_principal_and_membership()
    {
        await using var s = Store.LightweightSession();
        var directory = new PrincipalDirectory(s);

        // First login: sub + old email → a principal that owns a list (membership keyed by its id).
        var first = await directory.ResolveOrProvisionAsync("authentik-sub-email-change", "old@x.test", "Sam");
        var listId = Guid.CreateVersion7();
        s.Events.StartStream<TodoList>(listId, new ListCreated(listId, "L", ListKind.Todo, null, first.Id));
        await s.SaveChangesAsync();

        // The IdP email changes; the sub is stable → same principal, refreshed email.
        var second = await directory.ResolveOrProvisionAsync("authentik-sub-email-change", "new@x.test", "Sam");
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("new@x.test", second.Email);

        // Membership (keyed by the principal id) survives the email change.
        var access = await new AccessResolver(s)
            .RequireMembershipAsync(listId, first.Id, ListRole.Owner, CancellationToken.None);
        Assert.True(access.Allowed);
    }

    [Fact]
    public async Task Events_reference_principal_ids_not_emails_and_survive_identity_deletion()
    {
        await using var s = Store.LightweightSession();
        var directory = new PrincipalDirectory(s);
        var owner = await directory.ResolveOrProvisionAsync("sub-gdpr-owner", "owner@x.test", null);
        var assignee = await directory.ResolveOrProvisionAsync("sub-gdpr-assignee", "assignee@x.test", null);

        var listId = Guid.CreateVersion7();
        s.Events.StartStream<TodoList>(listId, new ListCreated(listId, "L", ListKind.Todo, null, owner.Id));
        var itemId = Guid.CreateVersion7();
        s.SetHeader(EventActor.HeaderKey, owner.Id.ToString());
        s.Events.StartStream<Item>(itemId,
            new ItemAdded(itemId, listId, null, "T", "a0", DateTimeOffset.UtcNow, Guid.CreateVersion7()),
            new ItemAssigned(itemId, assignee.Id, DateTimeOffset.UtcNow, Guid.CreateVersion7()));
        await s.SaveChangesAsync();

        // The persisted assignment references the principal id — no email in the event payload.
        await using var q = Store.QuerySession();
        var assigned = (await q.Events.FetchStreamAsync(itemId)).Select(e => e.Data).OfType<ItemAssigned>().Single();
        Assert.Equal(assignee.Id, assigned.AssigneePrincipalId);

        // Deleting the identity document leaves the guid-keyed events fully intact (crypto-shred-ready).
        s.Delete<Principal>(assignee.Id);
        await s.SaveChangesAsync();
        await using var q2 = Store.QuerySession();
        var afterDelete = (await q2.Events.FetchStreamAsync(itemId)).Select(e => e.Data).OfType<ItemAssigned>().Single();
        Assert.Equal(assignee.Id, afterDelete.AssigneePrincipalId);
        Assert.Null(await q2.LoadAsync<Principal>(assignee.Id));
    }
}
