using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Application;

/// <summary>A validated share-link grant: scoped to exactly one list at one access level.</summary>
public sealed record ShareGrant(Guid ShareId, Guid ListId, ShareAccess Access, string Label);
