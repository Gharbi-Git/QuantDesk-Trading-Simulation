
namespace QuantDesk.Modules.Administration.Domain.Entities;

public sealed class Utilisateur
{
    // D02 UML: name and application status are local, not a Keycloak role.
    public string DisplayName { get; private set; } = string.Empty;
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public long AuthVersion { get; private set; } = 0;

    public void UpdateDisplayName(string value)
    {
        var clean = value?.Trim() ?? "";
        if (clean.Length is < 1 or > 150)
            throw new ArgumentException("Display name must be 1-150 characters", nameof(value));
        DisplayName = clean;
    }

    // Any access change invalidates the previously issued cookie tickets.
    public void InvalidateSessions() => AuthVersion = checked(AuthVersion + 1);
    public void Suspendre()
    {
        if (Status == UserStatus.Suspended) return;
        Status = UserStatus.Suspended;
        InvalidateSessions();
    }
    public void Activer()
    {
        if (Status == UserStatus.Active) return;
        Status = UserStatus.Active;
        InvalidateSessions();
    }
    public Guid Id { get; set; } = Guid.NewGuid();

    // Stable identity assigned by Keycloak.
    public string KeycloakSubject { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
