using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>Every tool refuses an argument its schema doesn't declare, before it runs (so this has no side effects).
/// The rules themselves are tested in LupiraGeoApi, which holds the reference copy of StrictToolArguments.</summary>
public sealed class McpToolArgumentsTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private async Task<McpClient> ConnectAsync()
    {
        var http = Factory.ApiClient("alice@x.test");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task Every_tool_rejects_an_undeclared_argument()
    {
        await using var mcp = await ConnectAsync();
        var tools = await mcp.ListToolsAsync();
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            var result = await mcp.CallToolAsync(tool.Name, new Dictionary<string, object?> { ["__undeclared"] = 1 });

            Assert.True(result.IsError, tool.Name);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.StartsWith($"Invalid arguments for '{tool.Name}': unknown __undeclared", text);
        }
    }

    [Fact]
    public async Task Declared_arguments_reach_the_tool()
    {
        await using var mcp = await ConnectAsync();
        var result = await mcp.CallToolAsync("list_my_lists", new Dictionary<string, object?>());

        Assert.NotEqual(true, result.IsError);
    }
}
