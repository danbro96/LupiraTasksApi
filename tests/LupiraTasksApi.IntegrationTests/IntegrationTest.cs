using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Dtos.Lists;
using LupiraTasksApi.Dtos.Shares;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Base for integration tests: shares the container fixture, resets Marten data before each test, and provides
/// auth-aware HTTP clients + REST/DAV fixture helpers. Lives in the "integration" collection so tests run serially
/// against the shared DB. Mirrors LupiraCalApi's <c>IntegrationTest</c>.
/// </summary>
[Collection("integration")]
public abstract class IntegrationTest(TasksApiTestFactory factory) : IAsyncLifetime
{
    protected readonly TasksApiTestFactory Factory = factory;

    /// <summary>Matches the host's wire format: camelCase + string enums + case-insensitive — so typed
    /// (de)serialization of requests/responses round-trips exactly as the API emits/accepts them.</summary>
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected IDocumentStore Store => Factory.Store;

    public async Task InitializeAsync() => await Factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- service/store-level access (for behaviors not exposed over HTTP) ----

    protected async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = Factory.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    // ---- HTTP helpers ----

    /// <summary>Send a JSON request, optionally carrying an <c>Idempotency-Key</c> header.</summary>
    protected static async Task<HttpResponseMessage> SendJson(
        HttpClient client, HttpMethod method, string url, object? body = null, Guid? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (idempotencyKey is { } k) req.Headers.TryAddWithoutValidation("Idempotency-Key", k.ToString());
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return await client.SendAsync(req);
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<T>(Json))!;

    // ---- REST fixture helpers ----

    protected static async Task<ListResponse> CreateListAsync(HttpClient api, string name = "Groceries", ListKind kind = ListKind.Todo)
    {
        var resp = await SendJson(api, HttpMethod.Post, "/lists",
            new CreateListRequest { Id = Guid.CreateVersion7(), Name = name, Kind = kind });
        resp.EnsureSuccessStatusCode();
        return await ReadAsync<ListResponse>(resp);
    }

    protected static async Task<ItemResponse> CreateItemAsync(HttpClient api, Guid listId, string title = "Milk", string sortOrder = "a0")
    {
        var resp = await SendJson(api, HttpMethod.Post, $"/lists/{listId}/items",
            new CreateItemRequest { Id = Guid.CreateVersion7(), Title = title, SortOrder = sortOrder });
        resp.EnsureSuccessStatusCode();
        return await ReadAsync<ItemResponse>(resp);
    }

    protected static async Task<ShareResponse> MintShareLinkAsync(HttpClient api, Guid listId, ShareAccess access, string label = "fridge")
    {
        var resp = await SendJson(api, HttpMethod.Post, $"/lists/{listId}/shares",
            new CreateShareRequest { Access = access, Label = label });
        resp.EnsureSuccessStatusCode();
        return await ReadAsync<ShareResponse>(resp);
    }

    // ---- payload builders ----

    protected static string MinimalVtodo(string uid, string summary) =>
        new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//lupira-test//EN\r\nBEGIN:VTODO\r\n")
            .Append($"UID:{uid}\r\nSUMMARY:{summary}\r\n")
            .Append("END:VTODO\r\nEND:VCALENDAR\r\n")
            .ToString();
}
