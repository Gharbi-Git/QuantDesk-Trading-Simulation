using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using QuantDesk.Api.Authorization;
using Xunit;

namespace QuantDesk.IntegrationTests;

public class ResourceOwnerAuthorizationTests
{
    private static ClaimsPrincipal CreateUser(
        Guid userId,
        bool authenticated = true)
    {
        var claims = new[]
        {
            new Claim("quantdesk_user_id", userId.ToString()),
            new Claim(ClaimTypes.Role, "PLAYER")
        };

        return new ClaimsPrincipal(
            new ClaimsIdentity(
                claims,
                authenticated ? "TestAuthentication" : null));
    }

    private static async Task<bool> IsAuthorizedAsync(
        ClaimsPrincipal user,
        Guid ownerId)
    {
        var requirement = new ResourceOwnerRequirement();

        var context = new AuthorizationHandlerContext(
            new[] { requirement },
            user,
            new OwnedResource(ownerId));

        var handler = new ResourceOwnerAuthorizationHandler();

        await handler.HandleAsync(context);

        return context.HasSucceeded;
    }

    [Fact]
    public async Task Owner_CanAccessOwnResource()
    {
        var playerId = Guid.NewGuid();

        var allowed = await IsAuthorizedAsync(
            CreateUser(playerId),
            playerId);

        Assert.True(allowed);
    }

    [Fact]
    public async Task Player_CannotAccessAnotherPlayersResource()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();

        var allowed = await IsAuthorizedAsync(
            CreateUser(playerA),
            playerB);

        Assert.False(allowed);
    }

    [Fact]
    public async Task AnonymousUser_CannotAccessResource()
    {
        var playerId = Guid.NewGuid();

        var allowed = await IsAuthorizedAsync(
            CreateUser(playerId, authenticated: false),
            playerId);

        Assert.False(allowed);
    }

    [Fact]
    public async Task MissingUserIdClaim_DeniesAccess()
    {
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Role, "PLAYER") },
                "TestAuthentication"));

        var allowed = await IsAuthorizedAsync(
            user,
            Guid.NewGuid());

        Assert.False(allowed);
    }
}