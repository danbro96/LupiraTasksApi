using Lupira.Testing.Mcp;
using Lupira.Testing.Postgres;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

[Collection("integration")]
public sealed class McpToolArgumentsTests(TasksApiTestFactory factory) : McpStrictArgumentsTests
{
    protected override HttpClient CreateAuthenticatedClient() => factory.ApiClient("alice@x.test");

    protected override string DeclaredToolName => "list_my_lists";

    public override Task InitializeAsync() => factory.ResetAsync();
}
