using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using Xunit;

namespace LupiraTasksApi.UnitTests;

/// <summary>
/// Pure role-ordering math behind <see cref="AccessResolver"/>. The enum is ordered
/// Owner(0) &gt; Editor(1) &gt; Viewer(2), so a lower numeric value is a higher privilege
/// and <see cref="AccessResolver.Satisfies"/> must use &lt;=. These are the rules that
/// decide whether a member meets an endpoint's minimum role (below it ⇒ 404, not 403).
/// </summary>
public class AccessResolverTests
{
    [Theory]
    // Owner satisfies every minimum.
    [InlineData(ListRole.Owner, ListRole.Owner, true)]
    [InlineData(ListRole.Owner, ListRole.Editor, true)]
    [InlineData(ListRole.Owner, ListRole.Viewer, true)]
    // Editor satisfies Editor/Viewer but not Owner.
    [InlineData(ListRole.Editor, ListRole.Owner, false)]
    [InlineData(ListRole.Editor, ListRole.Editor, true)]
    [InlineData(ListRole.Editor, ListRole.Viewer, true)]
    // Viewer satisfies only Viewer.
    [InlineData(ListRole.Viewer, ListRole.Owner, false)]
    [InlineData(ListRole.Viewer, ListRole.Editor, false)]
    [InlineData(ListRole.Viewer, ListRole.Viewer, true)]
    public void Satisfies_respects_role_hierarchy(ListRole actual, ListRole required, bool expected) =>
        Assert.Equal(expected, AccessResolver.Satisfies(actual, required));
}
