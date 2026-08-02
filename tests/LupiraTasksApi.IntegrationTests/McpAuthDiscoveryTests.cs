using System.Net;
using System.Text.Json;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>MCP auth discovery (RFC 9728): anonymous metadata names the issuer, and a 401 on /mcp points at it.</summary>
public sealed class McpAuthDiscoveryTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task Metadata_is_anonymous_and_names_the_issuer(string path)
    {
        var anon = Factory.AnonymousClient();
        var resp = await anon.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("http://localhost/mcp", doc.RootElement.GetProperty("resource").GetString());
        var servers = doc.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["https://auth.test/application/o/lupira-tasks/"], servers);
        Assert.Contains("offline_access",
            doc.RootElement.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Unauthenticated_mcp_401_advertises_the_resource_metadata()
    {
        var anon = Factory.AnonymousClient();
        var resp = await anon.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var challenge = Assert.Single(resp.Headers.WwwAuthenticate).ToString();
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"", challenge);
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/mcp")]
    public async Task Tunnelled_requests_get_404(string path)
    {
        var anon = Factory.AnonymousClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("CF-Ray", "test-ray");
        var resp = await anon.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Rest_401_does_not_advertise_mcp_metadata()
    {
        var anon = Factory.AnonymousClient();
        var resp = await anon.GetAsync("/lists");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain(resp.Headers.WwwAuthenticate.Select(h => h.ToString()),
            c => c.Contains("resource_metadata"));
    }
}
