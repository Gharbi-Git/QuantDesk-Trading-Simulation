using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuantDesk.Api.Admin;

public sealed record ManagedUser(string Id, string Username, string? Email, bool Enabled, string Role);

/// <summary>
/// Server-to-server access to Keycloak Admin REST. NEVER send management credentials
/// or tokens to React. Requires a dedicated confidential service-account client.
/// </summary>
public sealed class KeycloakAdminRoleService(HttpClient http, IConfiguration configuration, ILogger<KeycloakAdminRoleService> logger)
{
    private string Realm => configuration["Keycloak:ManagementRealm"] ?? "quantdesk";
    private string BaseUrl => (configuration["Keycloak:ManagementBaseUrl"] ??
        throw new InvalidOperationException("Keycloak:ManagementBaseUrl not configured")).TrimEnd('/');
    private string ClientId => configuration["Keycloak:ManagementClientId"] ??
        throw new InvalidOperationException("Keycloak:ManagementClientId not configured");
    private string ClientSecret => configuration["Keycloak:ManagementClientSecret"] ??
        throw new InvalidOperationException("Keycloak:ManagementClientSecret not configured");
    private string AdminApi => $"{BaseUrl}/admin/realms/{Uri.EscapeDataString(Realm)}";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Keycloak:ManagementBaseUrl"]) &&
        !string.IsNullOrWhiteSpace(configuration["Keycloak:ManagementClientId"]) &&
        !string.IsNullOrWhiteSpace(configuration["Keycloak:ManagementClientSecret"]);

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret
        });
        using var response = await http.PostAsync($"{BaseUrl}/realms/{Uri.EscapeDataString(Realm)}/protocol/openid-connect/token", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return payload.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Keycloak returned no access token");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, ct);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Unexpected empty Keycloak response");
    }

    private sealed record RoleDto(string Id, string Name);
    private sealed record UserDto(string Id, string Username, string? Email, bool Enabled);

    private static string EffectiveRole(IEnumerable<RoleDto> roles)
    {
        var set = roles.Select(role => role.Name).ToHashSet(StringComparer.Ordinal);
        return set.Contains("SUPERUSER") ? "SUPERUSER"
            : set.Contains("ADMIN") ? "ADMIN"
            : set.Contains("PLAYER") ? "PLAYER" : "NONE";
    }

    private async Task<List<RoleDto>> GetRolesAsync(string userId, CancellationToken ct) =>
        await GetJsonAsync<List<RoleDto>>($"{AdminApi}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm/composite", ct);

    public async Task<string> GetRoleForSubjectAsync(string subject, CancellationToken ct)
    {
        if (!Guid.TryParse(subject, out _)) return "NONE";
        return EffectiveRole(await GetRolesAsync(subject, ct));
    }

    public async Task<bool> IsCurrentSuperuserAsync(string subject, CancellationToken ct)
    {
        return await GetRoleForSubjectAsync(subject, ct) == "SUPERUSER";
    }

    public async Task<IReadOnlyList<ManagedUser>> ListUsersAsync(string? search, CancellationToken ct)
    {
        var uri = $"{AdminApi}/users?first=0&max=50";
        if (!string.IsNullOrWhiteSpace(search)) uri += $"&search={Uri.EscapeDataString(search)}";
        var users = await GetJsonAsync<List<UserDto>>(uri, ct);
        var result = new List<ManagedUser>();
        foreach (var user in users)
        {
            if (user.Username.StartsWith("service-account-", StringComparison.OrdinalIgnoreCase)) continue;
            var role = EffectiveRole(await GetRolesAsync(user.Id, ct));
            result.Add(new ManagedUser(user.Id, user.Username, user.Email, user.Enabled, role));
        }
        return result;
    }

    public async Task<string> SetAdminAsync(string currentSubject, string targetSubject, bool assign, CancellationToken ct)
    {
        if (!Guid.TryParse(currentSubject, out var actorId))
            throw new InvalidOperationException("Invalid authenticated identity");
        if (!Guid.TryParse(targetSubject, out var targetId))
            throw new InvalidOperationException("Invalid Keycloak target identity");
        if (actorId == targetId)
            throw new InvalidOperationException("Cannot change your own administrator assignment");

        var userId = targetId.ToString("D");
        // Verify effective roles before any change, including never changing a SUPERUSER.
        var currentRole = EffectiveRole(await GetRolesAsync(userId, ct));
        if (currentRole is "SUPERUSER" or "NONE")
            throw new InvalidOperationException("Target must be an ordinary PLAYER or ADMIN; SUPERUSER and service identities are protected");
        if ((currentRole == "ADMIN") == assign) return currentRole;

        var role = await GetJsonAsync<RoleDto>($"{AdminApi}/roles/ADMIN", ct);
        using var response = await SendAsync(assign ? HttpMethod.Post : HttpMethod.Delete,
            $"{AdminApi}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm",
            new[] { role }, ct);
        response.EnsureSuccessStatusCode();

        var after = EffectiveRole(await GetRolesAsync(userId, ct));
        var expected = assign ? "ADMIN" : "PLAYER";
        if (after != expected)
            throw new InvalidOperationException("Keycloak did not confirm the requested effective role");

        logger.LogWarning("SECURITY_AUDIT role_change actor={Actor} target={Target} action={Action} new_role={Role}",
            actorId, targetId, assign ? "GRANT_ADMIN" : "REVOKE_ADMIN", after);
        return after;
    }
}
