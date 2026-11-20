
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using QuantDesk.Modules;
using QuantDesk.Modules.Administration.Infrastructure.Services;
using QuantDesk.Modules.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

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

// -------------------------------------------------------
// Authentication: Cookies + OpenID Connect
// -------------------------------------------------------

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
                    if (role is not ("PLAYER" or "ADMIN"))
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
        };
    });

// -------------------------------------------------------
// Authorization policies
// -------------------------------------------------------

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PlayerOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("PLAYER"));

    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("ADMIN"));
});

// -------------------------------------------------------
// Application middleware
// -------------------------------------------------------

var app = builder.Build();

app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

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
            RedirectUri = "/api/me"
        },
        new[]
        {
            OpenIdConnectDefaults.AuthenticationScheme
        }
    ));

// Return the currently authenticated player.
app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    return Results.Ok(new
    {
        authenticated = true,
        subject = user.FindFirst("sub")?.Value,
        quantDeskUserId = user.FindFirst("quantdesk_user_id")?.Value,
        username = user.Identity?.Name,
        email = user.FindFirst("email")?.Value
    });
})
.RequireAuthorization();

// End the local session and initiate Keycloak logout.
// TODO: Add antiforgery protection before frontend integration.
app.MapPost("/auth/logout", () =>
    Results.SignOut(
        new AuthenticationProperties
        {
            RedirectUri = "/health"
        },
        new[]
        {
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme
        }
    )
)
.RequireAuthorization();

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

// -------------------------------------------------------
// Start application
// -------------------------------------------------------

app.Run();

public partial class Program { }
