
using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Infrastructure.Persistence;

namespace QuantDesk.Modules.Administration.Infrastructure.Services;

public sealed class UtilisateurProvisioningService
{
    private readonly QuantDeskDbContext _db;

    public UtilisateurProvisioningService(QuantDeskDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> ProvisionAsync(
        string subject,
        string? username,
        string? email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject) ||
            subject.Length > 255)
        {
            throw new ArgumentException(
                "Invalid Keycloak subject.", nameof(subject));
        }

        var displayName = string.IsNullOrWhiteSpace(username)
            ? subject
            : username;

        var userEmail = string.IsNullOrWhiteSpace(email)
            ? null
            : email;

        if (displayName.Length > 150 ||
            userEmail?.Length > 320)
        {
            throw new ArgumentException(
                "Keycloak user information exceeds database limits.");
        }

        var newId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        // Atomic PostgreSQL upsert.
        // Concurrent logins cannot create duplicate users.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO utilisateurs
                (id, keycloak_subject, username, email, created_at_utc)
            VALUES
                ({newId}, {subject}, {displayName}, {userEmail}, {createdAt})
            ON CONFLICT (keycloak_subject)
            DO UPDATE SET
                username = EXCLUDED.username,
                email = EXCLUDED.email;
            """,
            cancellationToken);

        // Return the stable internal ID, whether inserted or reused.
        return await _db.Utilisateurs
            .AsNoTracking()
            .Where(user => user.KeycloakSubject == subject)
            .Select(user => user.Id)
            .SingleAsync(cancellationToken);
    }
}
