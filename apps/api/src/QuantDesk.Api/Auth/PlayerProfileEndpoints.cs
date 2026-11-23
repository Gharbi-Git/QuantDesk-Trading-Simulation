using Microsoft.AspNetCore.Antiforgery;
using QuantDesk.Modules.Administration.Infrastructure.Services;

namespace QuantDesk.Api.Auth;

public sealed record RenameProfileRequest(string PseudoPublic);

public static class PlayerProfileEndpoints
{
    public static IEndpointRouteBuilder MapPlayerProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/me/profile", async (RenameProfileRequest request,
            HttpContext ctx, IAntiforgery antiforgery,
            UserIdentityLifecycleService identity, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx))
                return Results.BadRequest(new { error = "Invalid CSRF token" });
            if (!Guid.TryParse(ctx.User.FindFirst("quantdesk_user_id")?.Value, out var id))
                return Results.Unauthorized();
            try
            {
                var found = await identity.RenamePublicProfileAsync(id, request.PseudoPublic, ct);
                return found ? Results.Ok(new { pseudoPublic = request.PseudoPublic.Trim() }) : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("PlayerOnly");
        return app;
    }
}
