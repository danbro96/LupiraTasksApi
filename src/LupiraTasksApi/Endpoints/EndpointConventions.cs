namespace LupiraTasksApi.Endpoints;

internal static class EndpointConventions
{
    /// <summary>Marks the endpoint as accepting the <c>Idempotency-Key</c> header (documented in OpenAPI).</summary>
    public static TBuilder WithIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IdempotentMutation());
}
