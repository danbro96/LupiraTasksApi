using System.Diagnostics;
using JasperFx.Events;
using Marten;

namespace LupiraTasksApi.Domain;

/// <summary>
/// Reads and stamps event provenance. The acting principal rides the <c>actor</c> header
/// (the caller's email, or <c>share:{label}</c> for anonymous share-link writes); aggregates
/// surface it on attribution fields. Causation = the originating command id; correlation = the
/// ambient OTel trace id, so every event links back to the request that produced it. All three
/// are unbackfillable, so they are stamped on every mutation. Requires the matching
/// <c>opts.Events.MetadataConfig</c> flags (headers + causation + correlation).
/// </summary>
public static class EventActor
{
    public const string HeaderKey = "actor";

    /// <summary>The actor header value, or <c>null</c> when none was stamped.</summary>
    public static string? Of(IEvent e) =>
        e.Headers is { } h && h.TryGetValue(HeaderKey, out var v) ? v as string : null;

    /// <summary>
    /// Stamps actor + causation + correlation onto the session before its single commit, so every
    /// event appended in this unit of work carries them. Causation is the command id (the direct
    /// cause); correlation is the current trace id when a trace is active (the enclosing request).
    /// </summary>
    public static void Stamp(IDocumentSession session, string actor, Guid commandId)
    {
        session.SetHeader(HeaderKey, actor);
        session.CausationId = commandId.ToString();
        if (Activity.Current?.TraceId is { } trace && trace != default)
            session.CorrelationId = trace.ToString();
    }
}
