using Lupira.Testing.Mcp;
using Lupira.Testing.Postgres;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

[Collection("integration")]
public sealed class McpAuthDiscoveryTests(TasksApiTestFactory factory) : McpResourceMetadataTests
{
    protected override HttpClient CreateAnonymousClient() => factory.AnonymousClient();

    protected override string Issuer => factory.Authority!;

    protected override string RestProbePath => "/lists";

    public override Task InitializeAsync() => factory.ResetAsync();
}
