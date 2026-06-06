using LupiraTasksApi.Auth;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Xunit;

namespace LupiraTasksApi.Tests;

/// <summary>
/// <see cref="CurrentUser"/> reads the validated JWT principal off the request. The
/// bearer is configured with NameClaimType = "email" and RoleClaimType = "groups", so
/// these tests fabricate a principal with those claim types and assert email + admin
/// resolution without any DB or HTTP server.
/// </summary>
public class CurrentUserTests
{
    private static CurrentUser ForClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "Bearer", nameType: "email", roleType: "groups");
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new CurrentUser(accessor);
    }

    [Fact]
    public void Email_comes_from_the_email_claim_and_is_trimmed()
    {
        var user = ForClaims(new Claim("email", "  alice@x.test  "));
        Assert.True(user.IsAuthenticated);
        Assert.Equal("alice@x.test", user.Email);
        Assert.Equal("alice@x.test", user.RequireEmail());
    }

    [Fact]
    public void IsAdmin_true_when_in_an_admin_group()
    {
        var user = ForClaims(
            new Claim("email", "boss@x.test"),
            new Claim("groups", "tasks-users"),
            new Claim("groups", "platform-admins"));

        Assert.True(user.IsAdmin);
        Assert.Equal(new[] { "tasks-users", "platform-admins" }, user.Groups);
    }

    [Fact]
    public void IsAdmin_false_for_ordinary_members()
    {
        var user = ForClaims(new Claim("email", "member@x.test"), new Claim("groups", "tasks-users"));
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public void Unauthenticated_principal_has_no_email()
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var user = new CurrentUser(new HttpContextAccessor { HttpContext = ctx });

        Assert.False(user.IsAuthenticated);
        Assert.Null(user.Email);
        Assert.Throws<InvalidOperationException>(() => user.RequireEmail());
    }
}
