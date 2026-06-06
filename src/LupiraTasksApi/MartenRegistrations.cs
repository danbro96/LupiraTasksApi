using JasperFx.Events.Projections;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using Marten;

namespace LupiraTasksApi;

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

        // Plain identity cache, keyed by email.
        opts.Schema.For<UserProfile>().Identity(x => x.Id);

        // Idempotency ledger keyed by command id.
        opts.Schema.For<ProcessedCommand>().Identity(c => c.CommandId);
    }
}
