using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using QuantDesk.Modules;
using QuantDesk.Modules.Administration.Infrastructure.Services;
using QuantDesk.Modules.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using QuantDesk.Api.Authorization;
using QuantDesk.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Api.Portfolio;
using QuantDesk.Api.Auth;
using QuantDesk.Api.Admin;
using QuantDesk.Modules.Administration.Domain.Entities;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddSingleton<
    IUserIdProvider,
    QuantDeskUserIdProvider>();

builder.Services.AddScoped<PlayerNotificationPublisher>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;

    options.Cookie.SecurePolicy =
        builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
});

// -------------------------------------------------------
// PostgreSQL / Entity Framework Core configuration
// -------------------------------------------------------

var connectionString =
    builder.Configuration.GetConnectionString("QuantDesk")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:QuantDesk");

builder.Services.AddDbContext<QuantDeskDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<UtilisateurProvisioningService>();
builder.Services.AddScoped<UserIdentityLifecycleService>();
builder.Services.AddHttpClient<KeycloakAdminRoleService>();

// -------------------------------------------------------
// Keycloak configuration
// -------------------------------------------------------

var authority = builder.Configuration["Keycloak:Authority"]
    ?? throw new InvalidOperationException(
        "Missing Keycloak:Authority");

var clientId = builder.Configuration["Keycloak:ClientId"]
    ?? throw new InvalidOperationException(
        "Missing Keycloak:ClientId");

var clientSecret = builder.Configuration["Keycloak:ClientSecret"]
    ?? throw new InvalidOperationException(
        "Missing Keycloak:ClientSecret");
// Server-controlled destination after login/logout.
var frontendReturnUrl =
    builder.Configuration["Frontend:ReturnUrl"] ?? (builder.Environment.IsDevelopment() ? "http://127.0.0.1:5173/" : "/");

// -------------------------------------------------------
// Authentication: Cookies + OpenID Connect
// -------------------------------------------------------
builder.Services.AddSingleton<
    IAuthorizationHandler,
    ResourceOwnerAuthorizationHandler>();
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;

        // API endpoints should return 401, not redirect
        // users automatically to Keycloak.
        options.DefaultChallengeScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "QuantDesk.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // HTTP is permitted only for local development.
        options.Cookie.SecurePolicy =
            builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

        options.Events.OnValidatePrincipal = async context =>
        {
            if (!Guid.TryParse(context.Principal?.FindFirst("quantdesk_user_id")?.Value, out var id) ||
                !long.TryParse(context.Principal?.FindFirst("quantdesk_auth_version")?.Value, out var version))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            var lifecycle = context.HttpContext.RequestServices.GetRequiredService<UserIdentityLifecycleService>();
            var sessionState = await lifecycle.GetSessionStateAsync(
                id, version, context.HttpContext.RequestAborted);
            if (sessionState != IdentitySessionState.Current)
            {
                if (sessionState == IdentitySessionState.Suspended)
                    SessionNotice.SetSuspended(context.HttpContext, !builder.Environment.IsDevelopment());
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };

        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode =
                    StatusCodes.Status401Unauthorized;

                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode =
                    StatusCodes.Status403Forbidden;

                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;

        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;

        // Temporary compatibility setting for local testing.
        // Revisit PAR support before production deployment.
        options.PushedAuthorizationBehavior =
            PushedAuthorizationBehavior.Disable;

        options.RequireHttpsMetadata =
            !builder.Environment.IsDevelopment();

        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";

        options.SignInScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;

        options.MapInboundClaims = false;

        options.TokenValidationParameters.NameClaimType =
            "preferred_username";

        options.TokenValidationParameters.RoleClaimType =
            ClaimTypes.Role;

        // Keep OIDC tokens out of the browser session cookie.
        options.SaveTokens = false;

        // OIDC registration is initiated by the BFF (not the React client).
        // Keycloak handles credentials, email verification, and account creation.
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            if (context.Properties.Items.TryGetValue("quantdesk:register", out var requested)
                && requested == "true")
            {
                context.ProtocolMessage.Prompt = "create";
            }
            else if (context.Properties.Items.TryGetValue("quantdesk:force-login", out var forceLogin)
                     && forceLogin == "true")
            {
                // Do not silently reuse a suspended user's existing Keycloak SSO session.
                context.ProtocolMessage.Prompt = "login";
            }
            return Task.CompletedTask;
        };
        // Keycloak needs the client ID when no ID token hint is stored.
options.Events.OnRedirectToIdentityProviderForSignOut = context =>
{
    context.ProtocolMessage.ClientId = clientId;
    return Task.CompletedTask;
};

        // ---------------------------------------------------
        // Map Keycloak realm roles into ASP.NET role claims
        // ---------------------------------------------------

        options.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is not ClaimsIdentity identity)
            {
                return Task.CompletedTask;
            }

            // IMPORTANT:
            // Create a snapshot before adding any new claims.
            // Otherwise, modifying the identity during enumeration
            // causes "Collection was modified".
            var roleClaims = context.Principal
                .FindAll("qd_roles")
                .ToArray();

            foreach (var roleClaim in roleClaims)
            {
                var value = roleClaim.Value.Trim();

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                IEnumerable<string> roles;

                // Keycloak may provide roles as a JSON array:
                // ["PLAYER", "ADMIN"]
                if (value.StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(value);

                        if (document.RootElement.ValueKind !=
                            JsonValueKind.Array)
                        {
                            continue;
                        }

                        roles = document.RootElement
                            .EnumerateArray()
                            .Where(element =>
                                element.ValueKind == JsonValueKind.String)
                            .Select(element => element.GetString()!)
                            .ToArray();
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed role claims.
                        // Never grant access based on invalid data.
                        continue;
                    }
                }
                else
                {
                    // Keycloak may also provide separate claims:
                    // qd_roles = PLAYER
                    // qd_roles = ADMIN
                    roles = new[] { value };
                }

                foreach (var role in roles)
                {
                    // Only recognize the QuantDesk application roles.
                    if (role is not ("PLAYER" or "ADMIN" or "SUPERUSER"))
                    {
                        continue;
                    }

                    // Avoid duplicate role claims.
                    if (!identity.HasClaim(ClaimTypes.Role, role))
                    {
                        identity.AddClaim(
                            new Claim(ClaimTypes.Role, role));
                    }
                }
            }

            return Task.CompletedTask;
        };

        // ---------------------------------------------------
        // Create or reuse the local QuantDesk user
        // after successful Keycloak authentication
        // ---------------------------------------------------

        options.Events.OnTicketReceived = async context =>
        {
            if (context.Principal?.Identity is not ClaimsIdentity identity)
            {
                context.Fail("Authenticated identity is missing.");
                return;
            }

            // Read the subject from the authenticated Keycloak identity.
            var subject = context.Principal.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(subject))
            {
                context.Fail("Keycloak subject is missing.");
                return;
            }

            var username = context.Principal
                .FindFirst("preferred_username")?.Value;

            var email = context.Principal
                .FindFirst("email")?.Value;

            var provisioningService = context.HttpContext
                .RequestServices
                .GetRequiredService<UtilisateurProvisioningService>();

            var utilisateurId = await provisioningService.ProvisionAsync(
                subject,
                username,
                email,
                context.HttpContext.RequestAborted);

            // Store our INTERNAL QuantDesk user ID in the session.
            identity.AddClaim(new Claim(
                "quantdesk_user_id",
                utilisateurId.ToString("D")));

            var lifecycle = context.HttpContext.RequestServices.GetRequiredService<UserIdentityLifecycleService>();
            var role = QuantDeskRoles.EffectiveRole(context.Principal);
            if (role == QuantDeskRoles.None)
            {
                context.Fail("No QuantDesk application role was assigned. Check Keycloak realm defaults.");
                return;
            }
            IdentitySession state;
            try
            {
                state = await lifecycle.CompleteLoginAsync(
                    utilisateurId, context.Principal.FindFirst("name")?.Value,
                    username, role == QuantDeskRoles.Player, context.HttpContext.RequestAborted);
            }
            catch (SuspendedAccountException)
            {
                // No application session is issued. Return to the UI instead of an OIDC error page.
                context.HandleResponse();
                context.HttpContext.Response.Redirect(
                    QueryHelpers.AddQueryString(frontendReturnUrl, "authNotice", "suspended"));
                return;
            }
            identity.AddClaim(new Claim("quantdesk_auth_version",
                state.AuthVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            SessionNotice.Clear(context.HttpContext);
        };
    });

// -------------------------------------------------------
// Authorization policies
// -------------------------------------------------------

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PlayerOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => QuantDeskRoles.CanTrade(ctx.User)));

    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => QuantDeskRoles.CanAdminister(ctx.User)));

    options.AddPolicy("SuperuserOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => QuantDeskRoles.CanManageAdmins(ctx.User)));

    options.AddPolicy("ResourceOwner", policy =>
        policy.RequireAuthenticatedUser()
              .AddRequirements(new ResourceOwnerRequirement()));
});

// -------------------------------------------------------
// Application middleware
// -------------------------------------------------------

var app = builder.Build();

app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// -------------------------------------------------------
// Existing QuantDesk bootstrap endpoints
// -------------------------------------------------------

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "healthy",
        application = "QuantDesk.Api"
    }));

app.MapGet("/api/meta/modules", () =>
    Results.Ok(ModuleCatalog.All));

// -------------------------------------------------------
// Authentication endpoints
// -------------------------------------------------------

// Explicitly start the OpenID Connect login flow.
app.MapGet("/auth/login", () =>
    Results.Challenge(
        new AuthenticationProperties
        {
            RedirectUri = frontendReturnUrl,
            Items = { ["quantdesk:force-login"] = "true" }
        },
        new[]
        {
            OpenIdConnectDefaults.AuthenticationScheme
        }
    ));

// Start self-service registration through the same confidential OIDC flow.
// Only the Keycloak server handles passwords and registration decisions.
app.MapGet("/auth/register", () =>
    Results.Challenge(
        new AuthenticationProperties
        {
            RedirectUri = frontendReturnUrl,
            Items = { ["quantdesk:register"] = "true" }
        },
        new[] { OpenIdConnectDefaults.AuthenticationScheme }));

// This anonymous endpoint exposes only a short-lived UI notice, never account identity.
app.MapGet("/api/auth/session-notice", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new
    {
        suspended = context.Request.Cookies[SessionNotice.CookieName] == "suspended"
    });
}).AllowAnonymous();

// Current session: role is derived from Keycloak, account/profile/status from PostgreSQL.
app.MapGet("/api/me", async (ClaimsPrincipal user, QuantDeskDbContext db, CancellationToken ct) =>
{
    if (!Guid.TryParse(user.FindFirst("quantdesk_user_id")?.Value, out var id))
        return Results.Unauthorized();
    var localUser = await db.Utilisateurs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    if (localUser is null || localUser.Status != UserStatus.Active) return Results.Unauthorized();
    var playerProfile = await db.ProfilsJoueurs.AsNoTracking()
        .SingleOrDefaultAsync(x => x.UtilisateurId == id, ct);
    var role = QuantDeskRoles.EffectiveRole(user);
    return Results.Ok(new
    {
        authenticated = true,
        subject = user.FindFirst("sub")?.Value,
        quantDeskUserId = id,
        username = localUser.Username,
        displayName = localUser.DisplayName,
        email = localUser.Email,
        status = localUser.Status.ToString().ToUpperInvariant(),
        pseudoPublic = playerProfile?.PseudoPublic,
        role,
        isPlayer = role == QuantDeskRoles.Player,
        isAdmin = role is QuantDeskRoles.Admin or QuantDeskRoles.Superuser,
        isSuperuser = role == QuantDeskRoles.Superuser
    });
}).RequireAuthorization();

// End the local session and initiate Keycloak logout.
// Issue a CSRF token to the authenticated frontend.
app.MapGet("/api/auth/csrf",
    (HttpContext context, IAntiforgery antiforgery) =>
    {
        context.Response.Headers.CacheControl = "no-store";

        var tokens = antiforgery.GetAndStoreTokens(context);

        return Results.Ok(new
        {
            csrfToken = tokens.RequestToken
        });
    })
    .AllowAnonymous();


// End the local session and initiate Keycloak logout.
// This route must remain available after a suspended cookie is rejected.
// CSRF protection is required even for anonymous callers.
app.MapPost("/auth/logout",
    async (HttpContext context, IAntiforgery antiforgery) =>
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest(new
            {
                error = "Invalid or missing CSRF token."
            });
        }

        // A suspended local session may already be invalid. Clear any stale notice
        // and end the Keycloak browser session so the next login can switch users.
        SessionNotice.Clear(context);

        return Results.SignOut(
            new AuthenticationProperties
            {
                RedirectUri = frontendReturnUrl
            },
            new[]
            {
                CookieAuthenticationDefaults.AuthenticationScheme,
                OpenIdConnectDefaults.AuthenticationScheme
            });
    })
    .AllowAnonymous();

// -------------------------------------------------------
// Temporary authorization test endpoints
// -------------------------------------------------------

// Only users assigned the PLAYER role may access this.
app.MapGet("/api/player/test", () =>
    Results.Ok(new
    {
        message = "PLAYER access granted"
    }))
    .RequireAuthorization("PlayerOnly");

// Only users assigned the ADMIN role may access this.
app.MapGet("/api/admin/test", () =>
    Results.Ok(new
    {
        message = "ADMIN access granted"
    }))
    .RequireAuthorization("AdminOnly");
// Server-controlled return destination after login/logout.
// For local Vite development, set Frontend__ReturnUrl.


app.MapHub<PlayerHub>("/hubs/player")
   .RequireAuthorization("PlayerOnly");


//
// Personal trading accounts (virtual funds only)
//

// Create an account for the authenticated player.
app.MapPost("/api/accounts/personal",
    async (
        HttpContext context,
        IAntiforgery antiforgery,
        QuantDeskDbContext db,
        CancellationToken ct) =>
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest(new
            {
                error = "Invalid or missing CSRF token."
            });
        }

        var claim = context.User
            .FindFirst("quantdesk_user_id")?.Value;

        if (!Guid.TryParse(claim, out var userId))
            return Results.Unauthorized();

        // One personal account per player.
        var existing = await db.ComptesTrading
            .AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.OwnerUserId == userId &&
                a.AccountType == "Personal", ct);

        if (existing is not null)
        {
            return Results.Ok(new
            {
                existing.Id,
                existing.AccountType,
                existing.Currency,
                existing.CashBalance
            });
        }

        // The server controls the initial virtual funding.
        const decimal startingCash = 100_000m;

        var account = CompteTrading.OpenPersonal(
            userId,
            startingCash);

        db.ComptesTrading.Add(account);

        try
        {
            // Account, portfolio and initial journal entry
            // are saved in the same transaction.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException pg &&
            pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new
            {
                error = "The personal account already exists."
            });
        }

        return Results.Created(
            $"/api/accounts/{account.Id}",
            new
            {
                account.Id,
                account.AccountType,
                account.Currency,
                account.CashBalance
            });
    })
    .RequireAuthorization("PlayerOnly");


// List only accounts belonging to the signed-in player.
app.MapGet("/api/accounts",
    async (
        ClaimsPrincipal user,
        QuantDeskDbContext db,
        CancellationToken ct) =>
    {
        var claim = user.FindFirst("quantdesk_user_id")?.Value;

        if (!Guid.TryParse(claim, out var userId))
            return Results.Unauthorized();

        var accounts = await db.ComptesTrading
            .AsNoTracking()
            .Where(a => a.OwnerUserId == userId)
            .Select(a => new
            {
                a.Id,
                a.AccountType,
                a.Currency,
                a.CashBalance,
                a.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Results.Ok(accounts);
    })
    .RequireAuthorization("PlayerOnly");


// Read an account only if the player owns it.
app.MapGet("/api/accounts/{id:guid}",
    async (
        Guid id,
        ClaimsPrincipal user,
        QuantDeskDbContext db,
        CancellationToken ct) =>
    {
        var claim = user.FindFirst("quantdesk_user_id")?.Value;

        if (!Guid.TryParse(claim, out var userId))
            return Results.Unauthorized();

        var account = await db.ComptesTrading
            .AsNoTracking()
            .Where(a =>
                a.Id == id &&
                a.OwnerUserId == userId)
            .Select(a => new
            {
                a.Id,
                a.AccountType,
                a.Currency,
                a.CashBalance,
                a.CreatedAtUtc
            })
            .FirstOrDefaultAsync(ct);

        // Don't reveal whether another player's account exists.
        return account is null
            ? Results.NotFound()
            : Results.Ok(account);
    })
    .RequireAuthorization("PlayerOnly");

// QD-056: Read holdings only if this session owns the account.
// No arbitrary Buy/Sell API: future M06 settlement will create/update positions.
app.MapGet("/api/accounts/{id:guid}/positions",
    async (
        Guid id,
        ClaimsPrincipal user,
        QuantDeskDbContext db,
        CancellationToken ct) =>
    {
        if (!Guid.TryParse(user.FindFirst("quantdesk_user_id")?.Value,
                out var userId))
            return Results.Unauthorized();

        var ownsAccount = await db.ComptesTrading
            .AsNoTracking()
            .AnyAsync(a => a.Id == id && a.OwnerUserId == userId, ct);

        if (!ownsAccount)
            return Results.NotFound();

        // Query is explicitly account-scoped even after ownership was checked.
        var positions = await db.PositionsAction
            .AsNoTracking()
            .Where(p => p.CompteTradingId == id)
            .OrderBy(p => p.InstrumentId)
            .ToListAsync(ct);

        return Results.Ok(positions.Select(p => new
        {
            p.Id,
            p.CompteTradingId,
            p.InstrumentId,
            p.Quantity,
            p.CostBasis,
            p.AverageAcquisitionCost,
            p.RealizedPnl
        }).ToArray());
    })
    .RequireAuthorization("PlayerOnly");

// Real, immutable funding entries. Other players cannot inspect this ledger.
app.MapGet("/api/accounts/{id:guid}/journal",
    async (
        Guid id,
        ClaimsPrincipal user,
        QuantDeskDbContext db,
        CancellationToken ct) =>
    {
        if (!Guid.TryParse(user.FindFirst("quantdesk_user_id")?.Value,
                out var userId))
            return Results.Unauthorized();

        var ownsAccount = await db.ComptesTrading
            .AsNoTracking()
            .AnyAsync(a => a.Id == id && a.OwnerUserId == userId, ct);

        if (!ownsAccount)
            return Results.NotFound();

        var entries = await db.CashJournal
            .AsNoTracking()
            .Where(j => j.CompteTradingId == id)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Select(j => new
            {
                j.Id,
                j.CompteTradingId,
                j.Amount,
                j.EventKey,
                j.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Results.Ok(entries);
    })
    .RequireAuthorization("PlayerOnly");

// -------------------------------------------------------
// Start application
// -------------------------------------------------------

app.MapPortfolioInsights();
app.MapPlayerProfileEndpoints();
app.MapIdentityAdministration();

app.Run();

public partial class Program { }

internal static class SessionNotice
{
    internal const string CookieName = "QuantDesk.SessionNotice";

    internal static void SetSuspended(HttpContext context, bool secure)
    {
        context.Response.Cookies.Append(CookieName, "suspended", new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = secure,
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(5)
        });
    }

    internal static void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
}
