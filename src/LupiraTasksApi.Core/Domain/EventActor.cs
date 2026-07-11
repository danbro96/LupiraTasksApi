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
    public const string EmailHeaderKey = "actor.email";
    public const string SourceHeaderKey = "source";

    /// <summary>The writing surface, stamped as the <c>source</c> header (matches cal-api).</summary>
    public const string SourceApi = "api";
    public const string SourceDav = "dav";

    /// <summary>The actor header value (a member's principal id, or <c>share:{label}</c>), or
    /// <c>null</c> when none was stamped.</summary>
    public static string? Of(IEvent e) =>
        e.Headers is { } h && h.TryGetValue(HeaderKey, out var v) ? v as string : null;

    /// <summary>
    /// Stamps provenance onto the session before its single commit, so every event appended in this unit
    /// of work carries it. <paramref name="actor"/> is the durable identity (principal id /
    /// <c>share:{label}</c>) — stamped both as the <c>actor</c> header (which aggregates project into
    /// attribution) and as Marten's <c>LastModifiedBy</c>. <paramref name="actorEmail"/> is a human-audit
    /// convenience header (<c>null</c> for a share write); <paramref name="source"/> records the writing
    /// surface (<see cref="SourceApi"/>/<see cref="SourceDav"/>). Causation is the command id; correlation
    /// is the current trace id when a trace is active.
    /// </summary>
    public static void Stamp(IDocumentSession session, string actor, string? actorEmail, Guid commandId, string source = SourceApi)
    {
        session.SetHeader(HeaderKey, actor);
        if (actorEmail is not null) session.SetHeader(EmailHeaderKey, actorEmail);
        session.SetHeader(SourceHeaderKey, source);
        session.LastModifiedBy = actor;
        session.CausationId = commandId.ToString();
        if (Activity.Current?.TraceId is { } trace && trace != default)
            session.CorrelationId = trace.ToString();
    }
}
