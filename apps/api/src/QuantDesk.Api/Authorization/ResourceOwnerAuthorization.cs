using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace QuantDesk.Api.Authorization;

// Represents the owner loaded from a trusted resource,
// such as a trading account, strategy, or Arena match.
public sealed record OwnedResource(Guid OwnerUserId);

// Requirement: the current user must own the resource.
public sealed class ResourceOwnerRequirement
    : IAuthorizationRequirement
{
}

// Checks ownership using the authenticated QuantDesk user ID.
public sealed class ResourceOwnerAuthorizationHandler
    : AuthorizationHandler<ResourceOwnerRequirement, OwnedResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceOwnerRequirement requirement,
        OwnedResource resource)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        var userIdClaim = context.User.FindFirst(
            "quantdesk_user_id")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Task.CompletedTask;
        }

        if (userId == resource.OwnerUserId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}