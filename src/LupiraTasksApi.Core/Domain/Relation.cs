using System.Security.Cryptography;
using System.Text;

namespace LupiraTasksApi.Domain;

/// <summary>
/// The single cross-API edge (plain Marten document, indexed by <see cref="FromId"/>): a by-reference link from a
/// task to something elsewhere — a cal-api Prompt heartbeat (<c>ToKind="cal-item"</c>), or an external ref such as a
/// GitHub issue/PR or a health incident (<c>ToKind="url"</c>). No FK — integrity is by convention. Mirrors cal-api's
/// <c>Relation</c> so the two services share one linking vocabulary, and is the keystone for the standing-monitor
/// pattern. NOT an event-sourced/LWW field on the item: the edge is its own document with structural idempotency
/// (see <see cref="DeterministicId"/>), so add/remove are naturally idempotent.
/// </summary>
public sealed class Relation
{
    /// <summary>The kind on the "from" side. In tasks-api the source is always a task.</summary>
    public const string TaskKind = "task";

    public Guid Id { get; set; }
    public string FromKind { get; set; } = TaskKind;
    public Guid FromId { get; set; }
    public string ToKind { get; set; } = "";       // e.g. "cal-item" | "url"
    public string ToRef { get; set; } = "";
    public string RelationType { get; set; } = ""; // e.g. "monitors" | "spawned-by" | "produced" | "blocked-by" | "relates-to"
    public string? Metadata { get; set; }          // free-form JSON

    /// <summary>
    /// The document identity, derived from the edge tuple so the same link always maps to the same id: re-adding is a
    /// race-free upsert (<c>Store</c>) and removing is an idempotent <c>Delete</c> by this id. A stable hash (SHA-256
    /// of the canonical tuple, first 16 bytes) rather than a random GUID — that is what makes add/remove idempotent.
    /// </summary>
    public static Guid DeterministicId(Guid fromId, string toKind, string toRef, string relationType)
    {
        var canonical = $"{fromId:N}\n{toKind}\n{toRef}\n{relationType}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
        return new Guid(hash[..16]);
    }
}
