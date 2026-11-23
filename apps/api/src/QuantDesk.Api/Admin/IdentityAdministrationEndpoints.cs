using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using QuantDesk.Api.Auth;
using QuantDesk.Modules.Administration.Domain.Entities;
using QuantDesk.Modules.Infrastructure.Persistence;

namespace QuantDesk.Api.Admin;

public sealed record ChangeUserStatusRequest(string Status);

public static class IdentityAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapIdentityAdministration(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/admin/users").RequireAuthorization("AdminOnly");
        users.MapGet("/", async (HttpContext ctx, QuantDeskDbContext db,
            KeycloakAdminRoleService keycloak, string? search, CancellationToken ct) =>
        {
            if (!keycloak.IsConfigured) return Results.Problem("Keycloak management connection is not configured", statusCode: 503);
            if (!await HasLiveRole(ctx.User, keycloak, ct)) return Results.Forbid();
            if (search?.Length > 100) return Results.BadRequest(new { error = "Search too long" });
            var query = db.Utilisateurs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                query = query.Where(x => x.Username.Contains(s) || x.DisplayName.Contains(s));
            }
            var rows = await query.OrderBy(x => x.Username).Take(50).ToListAsync(ct);
            var result = new List<object>();
            foreach (var row in rows)
            {
                var role = await keycloak.GetRoleForSubjectAsync(row.KeycloakSubject, ct);
                result.Add(new { id = row.Id, username = row.Username, displayName = row.DisplayName,
                    role, status = row.Status.ToString().ToUpperInvariant(), createdAtUtc = row.CreatedAtUtc });
            }
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result);
        });

        users.MapPut("/{id:guid}/status", async (Guid id, ChangeUserStatusRequest request,
            HttpContext ctx, IAntiforgery antiforgery, QuantDeskDbContext db,
            KeycloakAdminRoleService keycloak, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx))
                return Results.BadRequest(new { error = "Invalid CSRF token" });
            if (!keycloak.IsConfigured) return Results.Problem("Keycloak management connection is not configured", statusCode: 503);
            if (!await HasLiveRole(ctx.User, keycloak, ct)) return Results.Forbid();
            if (id.ToString("D") == ctx.User.FindFirst("quantdesk_user_id")?.Value)
                return Results.Conflict(new { error = "You cannot change your own status" });
            if (request.Status is not ("ACTIVE" or "SUSPENDED"))
                return Results.BadRequest(new { error = "Status must be ACTIVE or SUSPENDED" });

            var target = await db.Utilisateurs.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (target is null) return Results.NotFound();
            var targetRole = await keycloak.GetRoleForSubjectAsync(target.KeycloakSubject, ct);
            if (targetRole != QuantDeskRoles.Player)
                return Results.Conflict(new { error = "Only PLAYER accounts can be suspended by application admins" });
            if (request.Status == "ACTIVE") target.Activer();
            else target.Suspendre();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id, status = target.Status.ToString().ToUpperInvariant(), sessionsInvalidated = true });
        });

        var super = app.MapGroup("/api/superuser/users").RequireAuthorization("SuperuserOnly");
        super.MapPost("/{id:guid}/admin", async (Guid id, HttpContext ctx,
            IAntiforgery antiforgery, QuantDeskDbContext db, KeycloakAdminRoleService keycloak, CancellationToken ct) =>
            await SetAdmin(id, true, ctx, antiforgery, db, keycloak, ct));
        super.MapDelete("/{id:guid}/admin", async (Guid id, HttpContext ctx,
            IAntiforgery antiforgery, QuantDeskDbContext db, KeycloakAdminRoleService keycloak, CancellationToken ct) =>
            await SetAdmin(id, false, ctx, antiforgery, db, keycloak, ct));
        return app;
    }

    private static async Task<bool> HasLiveRole(ClaimsPrincipal caller,
        KeycloakAdminRoleService kc, CancellationToken ct)
    {
        var role = await kc.GetRoleForSubjectAsync(caller.FindFirst("sub")?.Value ?? "", ct);
        return role is QuantDeskRoles.Admin or QuantDeskRoles.Superuser;
    }

    private static async Task<IResult> SetAdmin(Guid id, bool assign, HttpContext ctx,
        IAntiforgery antiforgery, QuantDeskDbContext db, KeycloakAdminRoleService kc, CancellationToken ct)
    {
        if (!await antiforgery.IsRequestValidAsync(ctx))
            return Results.BadRequest(new { error = "Invalid CSRF token" });
        if (!kc.IsConfigured) return Results.Problem("Keycloak management connection is not configured", statusCode: 503);
        var actorSubject = ctx.User.FindFirst("sub")?.Value ?? "";
        if (!await kc.IsCurrentSuperuserAsync(actorSubject, ct)) return Results.Forbid();
        if (ctx.User.FindFirst("quantdesk_user_id")?.Value == id.ToString("D"))
            return Results.Conflict(new { error = "Cannot change own role" });

        var target = await db.Utilisateurs.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (target is null) return Results.NotFound();
        if (target.Status == UserStatus.Suspended)
            return Results.Conflict(new { error = "Reactivate the user before changing their role" });
        try
        {
            var updatedRole = await kc.SetAdminAsync(actorSubject, target.KeycloakSubject, assign, ct);
            // Force old sessions to re-authenticate with newly signed Keycloak role claims.
            target.InvalidateSessions();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id, role = updatedRole, sessionsInvalidated = true });
        }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }
}
