using JasperFx.Events.Projections;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Domain.Shares;
using Marten;

namespace LupiraTasksApi.Data;

public static class MartenRegistrations
{
    public static void Configure(StoreOptions opts)
    {
        // Store event-metadata headers so the "actor" header (set on each mutation)
        // is persisted and readable from aggregate Apply(IEvent<T>) methods for
        // attribution (CreatedBy / CompletedBy / Member.AddedBy).
        opts.Events.MetadataConfig.HeadersEnabled = true;

        // Single-stream event-sourced aggregates, projected inline (O(1) reads,
        // immediately consistent — no async daemon, no multi-stream projection).
        opts.Projections.Snapshot<TodoList>(SnapshotLifecycle.Inline);
        opts.Projections.Snapshot<Item>(SnapshotLifecycle.Inline);
        opts.Projections.Snapshot<ShareLink>(SnapshotLifecycle.Inline);

        // A share token is the opaque secret a recipient presents; consumption looks the link up
        // by it, so index it uniquely (the stream id is the non-secret ShareId).
        opts.Schema.For<ShareLink>().Index(x => x.Token, i => i.IsUnique = true);

        // The CalDAV surface resolves a resource by (ListId, Uid); index the read-through Uid so
        // those per-resource GET/PUT/DELETE lookups don't table-scan the items.
        opts.Schema.For<Item>().Index(x => x.Uid);

        // Plain identity cache, keyed by email.
        opts.Schema.For<UserProfile>().Identity(x => x.Id);

        // Idempotency ledger keyed by command id.
        opts.Schema.For<ProcessedCommand>().Identity(c => c.CommandId);
    }
}
