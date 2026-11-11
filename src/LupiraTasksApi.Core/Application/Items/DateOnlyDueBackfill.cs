using System.Security.Cryptography;
using System.Text;
using Lupira.Identity.Marten;
using Lupira.Marten.Idempotency;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Domain.Items.Events;
using Marten;
using Microsoft.Extensions.Options;
using NodaTime;

namespace LupiraTasksApi.Core.Application.Items;

/// <summary>One-off: a timed due at 23:59 local stood in for "that day", so it becomes a date-only due. The event is
/// appended one tick after the due's guard, so any later edit still wins and the sync feeds re-send the item.</summary>
public sealed class DateOnlyDueBackfill(IDocumentSession session, Idempotency idempotency, IOptions<ItemTimeZoneOptions> zones)
{
    public const string EndOfDay = "23:59";
    private const string Actor = "backfill:date-only-dues";
    private const int SamplesPerBucket = 5;
    private static readonly string[] Buckets = [EndOfDay, "00:00", "09:00", "other"];

    public async Task<DateOnlyDueBackfillReport> RunAsync(bool dryRun, CancellationToken ct)
    {
        var zone = DateTimeZoneProviders.Tzdb[zones.Value.DefaultTimezone];
        var items = await session.Query<Item>().Where(i => !i.Deleted).ToListAsync(ct);
        var report = new DateOnlyDueBackfillReport(zone.Id, dryRun, Buckets);

        foreach (var item in items.Where(i => i.DueAt is not null).OrderBy(i => i.Id))
        {
            var local = Instant.FromDateTimeOffset(item.DueAt!.Value).InZone(zone).LocalDateTime;
            var bucket = BucketOf(local);
            report.Counts[bucket]++;
            if (report.Samples[bucket].Count < SamplesPerBucket)
                report.Samples[bucket].Add($"{item.Id} {item.DueAt:O} → {local:yyyy-MM-dd HH:mm} {item.Title}");
            if (bucket != EndOfDay || dryRun) continue;

            var commandId = CommandIdFor(item.Id);
            if (await idempotency.SeenAsync(commandId, ct) is not null) continue;
            EventActor.Stamp(session, Actor, null, commandId);
            var due = new ItemDueDateSetV2(item.Id, local.Date.ToDateOnly(), DueAt: null, item.State.DueTs.AddTicks(1), commandId);
            if (await idempotency.AppendDedupAsync(commandId, item.Id, [due], ct) is not null) report.Converted++;
        }

        return report;
    }

    private static string BucketOf(LocalDateTime local) => (local.Hour, local.Minute) switch
    {
        (23, 59) => EndOfDay,
        (0, 0) => "00:00",
        (9, 0) => "09:00",
        _ => "other",
    };

    // Keyed on the item alone so a re-run dedups against the first run's ledger row.
    private static Guid CommandIdFor(Guid itemId)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"backfill-date-only-due\n{itemId:N}"), hash);
        return new Guid(hash[..16]);
    }
}
