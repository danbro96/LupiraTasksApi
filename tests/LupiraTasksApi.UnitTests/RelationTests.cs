using LupiraTasksApi.Domain;
using Xunit;

namespace LupiraTasksApi.UnitTests;

/// <summary>
/// The relation document's identity is derived from its edge tuple (<see cref="Relation.DeterministicId"/>),
/// which is what makes add idempotent (a race-free upsert) and remove idempotent (delete by the same id).
/// These vectors pin that derivation: stable for the same tuple, distinct for any differing component.
/// </summary>
public class RelationTests
{
    private static readonly Guid From = Guid.Parse("0190a000-0000-7000-8000-000000000001");
    private static readonly Guid OtherFrom = Guid.Parse("0190a000-0000-7000-8000-000000000002");

    [Fact]
    public void DeterministicId_is_stable_for_the_same_tuple()
    {
        var a = Relation.DeterministicId(From, "cal-item", "ref-1", "monitors");
        var b = Relation.DeterministicId(From, "cal-item", "ref-1", "monitors");
        Assert.Equal(a, b);
        Assert.NotEqual(Guid.Empty, a);
    }

    [Theory]
    [InlineData("url", "ref-1", "monitors")]       // differing toKind
    [InlineData("cal-item", "ref-2", "monitors")]  // differing toRef
    [InlineData("cal-item", "ref-1", "relates-to")] // differing relationType
    public void DeterministicId_differs_when_any_component_differs(string toKind, string toRef, string relationType)
    {
        var baseline = Relation.DeterministicId(From, "cal-item", "ref-1", "monitors");
        Assert.NotEqual(baseline, Relation.DeterministicId(From, toKind, toRef, relationType));
    }

    [Fact]
    public void DeterministicId_differs_by_from_id()
    {
        var a = Relation.DeterministicId(From, "cal-item", "ref-1", "monitors");
        var b = Relation.DeterministicId(OtherFrom, "cal-item", "ref-1", "monitors");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeterministicId_is_case_sensitive()
    {
        var lower = Relation.DeterministicId(From, "url", "ref-1", "monitors");
        var upper = Relation.DeterministicId(From, "URL", "ref-1", "monitors");
        Assert.NotEqual(lower, upper);
    }
}
