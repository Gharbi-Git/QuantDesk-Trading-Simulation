using System.Security.Claims;
using System.Text.Json;

namespace QuantDesk.Api.Auth;

/// <summary>
/// The Keycloak realm is the source of truth. These helpers only interpret
/// trusted, validated claims in a QuantDesk authentication ticket.
/// </summary>
public static class QuantDeskRoles
{
    public const string Player = "PLAYER";
    public const string Admin = "ADMIN";
    public const string Superuser = "SUPERUSER";
    public const string None = "NONE";

    // Keycloak realm role mapper writes qd_roles to the validated ID token.
    // It may arrive as a JSON array or as one claim per role.
    public static IReadOnlyCollection<string> ReadRealmRoles(ClaimsPrincipal principal)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in principal.FindAll("qd_roles"))
        {
            var raw = claim.Value.Trim();
            if (raw.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using var json = JsonDocument.Parse(raw);
                    if (json.RootElement.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in json.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            AddAllowed(item.GetString(), roles);
                    }
                }
                catch (JsonException) { /* Invalid claim must never grant roles. */ }
            }
            else
            {
                AddAllowed(raw, roles);
            }
        }
        return roles;
    }

    public static string EffectiveRole(ClaimsPrincipal principal)
    {
        // Cookie role claims are created only from a validated Keycloak token.
        // Unit tests may build principals with ClaimTypes.Role directly.
        if (principal.IsInRole(Superuser)) return Superuser;
        if (principal.IsInRole(Admin)) return Admin;
        if (principal.IsInRole(Player)) return Player;
        return None;
    }

    public static bool CanTrade(ClaimsPrincipal principal) => EffectiveRole(principal) == Player;
    public static bool CanAdminister(ClaimsPrincipal principal) => EffectiveRole(principal) is Admin or Superuser;
    public static bool CanManageAdmins(ClaimsPrincipal principal) => EffectiveRole(principal) == Superuser;

    private static void AddAllowed(string? role, ISet<string> roles)
    {
        if (role is Player or Admin or Superuser) roles.Add(role);
    }
}
