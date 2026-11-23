using System.Security.Claims;
using QuantDesk.Api.Auth;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class QuantDeskRolesTests
{
    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(x => new Claim(ClaimTypes.Role, x)), "TestAuth", ClaimTypes.Name, ClaimTypes.Role));

    [Fact] public void Player_can_trade_but_cannot_manage_platform()
    {
        var user = Principal("PLAYER");
        Assert.Equal("PLAYER", QuantDeskRoles.EffectiveRole(user));
        Assert.True(QuantDeskRoles.CanTrade(user));
        Assert.False(QuantDeskRoles.CanAdminister(user));
        Assert.False(QuantDeskRoles.CanManageAdmins(user));
    }

    [Fact] public void Admin_cannot_assign_other_admins_or_trade()
    {
        var user = Principal("PLAYER", "ADMIN");
        Assert.Equal("ADMIN", QuantDeskRoles.EffectiveRole(user));
        Assert.False(QuantDeskRoles.CanTrade(user));
        Assert.True(QuantDeskRoles.CanAdminister(user));
        Assert.False(QuantDeskRoles.CanManageAdmins(user));
    }

    [Fact] public void Superuser_has_highest_precedence_and_can_manage_admins()
    {
        var user = Principal("PLAYER", "ADMIN", "SUPERUSER");
        Assert.Equal("SUPERUSER", QuantDeskRoles.EffectiveRole(user));
        Assert.False(QuantDeskRoles.CanTrade(user));
        Assert.True(QuantDeskRoles.CanAdminister(user));
        Assert.True(QuantDeskRoles.CanManageAdmins(user));
    }

    [Fact] public void Unrecognized_role_grants_nothing()
    {
        var user = Principal("realm-management", "admin");
        Assert.Equal("NONE", QuantDeskRoles.EffectiveRole(user));
        Assert.False(QuantDeskRoles.CanTrade(user));
        Assert.False(QuantDeskRoles.CanAdminister(user));
    }

    [Fact] public void Parses_keycloak_json_role_claim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("qd_roles", "[\"PLAYER\",\"SUPERUSER\",\"offline_access\"]") }, "oidc"));
        var roles = QuantDeskRoles.ReadRealmRoles(principal);
        Assert.Contains("PLAYER", roles);
        Assert.Contains("SUPERUSER", roles);
        Assert.DoesNotContain("offline_access", roles);
    }

    [Fact] public void Parses_separate_claims_and_ignores_malformed_data()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("qd_roles", "PLAYER"),
            new Claim("qd_roles", "invalid"),
            new Claim("qd_roles", "[broken"),
            new Claim("qd_roles", "ADMIN") }, "oidc"));
        Assert.Equal(new[] { "ADMIN", "PLAYER" }, QuantDeskRoles.ReadRealmRoles(principal).OrderBy(x => x).ToArray());
    }
}
