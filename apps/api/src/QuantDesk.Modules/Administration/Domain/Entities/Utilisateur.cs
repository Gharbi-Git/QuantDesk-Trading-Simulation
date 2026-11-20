
namespace QuantDesk.Modules.Administration.Domain.Entities;

public sealed class Utilisateur
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Stable identity assigned by Keycloak.
    public string KeycloakSubject { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
