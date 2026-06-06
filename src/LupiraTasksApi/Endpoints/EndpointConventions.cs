namespace LupiraTasksApi.Endpoints;

/// <summary>
/// Endpoint metadata marker: tags a mutation as accepting the optional
/// <c>Idempotency-Key</c> header. An OpenAPI operation transformer in <c>Program.cs</c>
/// picks this up and documents the header, so the endpoint declarations stay thin and the
/// transformer logic lives in one place.
/// </summary>
public sealed class IdempotentMutation;

/// <summary>Shared endpoint conventions.</summary>
internal static class EndpointConventions
{
    /// <summary>Marks the endpoint as accepting the <c>Idempotency-Key</c> header (documented in OpenAPI).</summary>
    public static TBuilder WithIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IdempotentMutation());
}
