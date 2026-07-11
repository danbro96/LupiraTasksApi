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
        // Provenance is unbackfillable, so capture it on every event:
        //  * headers   — the "actor" header (caller email / share:{label}) → attribution fields.
        //  * causation — the originating command id.
        //  * correlation — the OTel trace id of the request that produced the event.
        // Stamped in EventActor.Stamp before each single commit.
        opts.Events.MetadataConfig.HeadersEnabled = true;
        opts.Events.MetadataConfig.CausationIdEnabled = true;
        opts.Events.MetadataConfig.CorrelationIdEnabled = true;

        // Explicit event-type aliases pin the durable storage name (the mt_events.type column) to a
        // string that is decoupled from the CLR type name — so an event record can be renamed or moved
        // without breaking deserialization of history. Each alias equals the current Marten default
        // (snake_case), so pinning them changes nothing in storage now; it only freezes the contract.
        // Evolving an event = a new versioned type mapped to the SAME alias + an upcaster (see
        // docs/architecture.md § Event evolution), never a trailing-optional field.
        MapEvents(opts);

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

        // Identity document, keyed by the internal principal id. Indexed by the durable Authentik
        // sub (the resolution anchor) and by the mutable login email (the OIDC/DAV/invite join key).
        opts.Schema.For<Principal>().Identity(x => x.Id).Index(x => x.AuthentikSub).Index(x => x.Email);

        // Idempotency ledger keyed by command id.
        opts.Schema.For<ProcessedCommand>().Identity(c => c.CommandId);

        // Cross-API links (plain document). Indexed by FromId so listing a task's relations doesn't
        // table-scan. The document id is the tuple-derived Relation.DeterministicId, so add/remove are idempotent.
        opts.Schema.For<Relation>().Index(x => x.FromId);
    }

    /// <summary>
    /// Pins every event's durable storage alias. Values equal the current snake_case default, so this is a
    /// no-op for existing data and a contract going forward: the CLR type may be renamed/relocated freely,
    /// and a breaking shape change becomes a new type mapped to the same alias plus an upcaster.
    /// </summary>
    private static void MapEvents(StoreOptions opts)
    {
        opts.Events.MapEventType<ItemAdded>("item_added");
        opts.Events.MapEventType<ItemRenamed>("item_renamed");
        opts.Events.MapEventType<ItemNotesEdited>("item_notes_edited");
        opts.Events.MapEventType<ItemAssigned>("item_assigned");
        opts.Events.MapEventType<ItemDueDateSet>("item_due_date_set");
        opts.Events.MapEventType<ItemTagAdded>("item_tag_added");
        opts.Events.MapEventType<ItemTagRemoved>("item_tag_removed");
        opts.Events.MapEventType<ItemQuantitySet>("item_quantity_set");
        opts.Events.MapEventType<ItemPrioritySet>("item_priority_set");
        opts.Events.MapEventType<ItemCompleted>("item_completed");
        opts.Events.MapEventType<ItemReopened>("item_reopened");
        opts.Events.MapEventType<ItemStatusChanged>("item_status_changed");
        opts.Events.MapEventType<ItemMoved>("item_moved");
        opts.Events.MapEventType<ItemMetadataSet>("item_metadata_set");
        opts.Events.MapEventType<ItemDeleted>("item_deleted");
        opts.Events.MapEventType<ItemVtodoPut>("item_vtodo_put");

        opts.Events.MapEventType<ListCreated>("list_created");
        opts.Events.MapEventType<ListRenamed>("list_renamed");
        opts.Events.MapEventType<ListRecolored>("list_recolored");
        opts.Events.MapEventType<ListSimplePrioritySet>("list_simple_priority_set");
        opts.Events.MapEventType<ListArchived>("list_archived");
        opts.Events.MapEventType<ListRestored>("list_restored");
        opts.Events.MapEventType<ListDeleted>("list_deleted");
        opts.Events.MapEventType<TagDefined>("tag_defined");
        opts.Events.MapEventType<TagRecolored>("tag_recolored");
        opts.Events.MapEventType<TagRemoved>("tag_removed");
        opts.Events.MapEventType<MemberAdded>("member_added");
        opts.Events.MapEventType<MemberRoleChanged>("member_role_changed");
        opts.Events.MapEventType<MemberRemoved>("member_removed");

        opts.Events.MapEventType<ShareLinkCreated>("share_link_created");
        opts.Events.MapEventType<ShareLinkRevoked>("share_link_revoked");
    }
}
