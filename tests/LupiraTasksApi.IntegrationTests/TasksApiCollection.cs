using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>One ephemeral Postgres container shared across the whole run; tests in this collection run serially
/// (they share DB state and reset it per test), which avoids cross-test interference. Mirrors LupiraCalApi.</summary>
[CollectionDefinition("integration")]
public sealed class TasksApiCollection : ICollectionFixture<TasksApiTestFactory>;
