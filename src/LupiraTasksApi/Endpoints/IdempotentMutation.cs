namespace LupiraTasksApi.Endpoints;

/// <summary>
/// Endpoint metadata marker: tags a mutation as accepting the optional
/// <c>Idempotency-Key</c> header. An OpenAPI operation transformer in <c>Program.cs</c>
/// picks this up and documents the header, so the endpoint declarations stay thin and the
/// transformer logic lives in one place.
/// </summary>
public sealed class IdempotentMutation;
