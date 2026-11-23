using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Administration.Domain.Entities;
using QuantDesk.Modules.Infrastructure.Persistence;

namespace QuantDesk.Modules.Administration.Infrastructure.Services;

public sealed record IdentitySession(long AuthVersion, UserStatus Status, string DisplayName);

public enum IdentitySessionState { Current, Expired, Suspended }

public sealed class SuspendedAccountException : InvalidOperationException
{
    public SuspendedAccountException()
        : base("This account is suspended. Contact a QuantDesk administrator.") { }
}

/// <summary>
/// Local identity state is NOT a replacement for Keycloak. All roles come from validated OIDC claims.
/// Status and version are checked on every authenticated HTTP/WebSocket handshake.
/// </summary>
public sealed class UserIdentityLifecycleService(QuantDeskDbContext db)
{
    public async Task<IdentitySession> CompleteLoginAsync(Guid userId, string? displayName,
        string? username, bool shouldCreatePlayerProfile, CancellationToken ct)
    {
        var user = await db.Utilisateurs.SingleAsync(x => x.Id == userId, ct);
        if (user.Status == UserStatus.Suspended)
            throw new SuspendedAccountException();

        var suggested = (displayName ?? "").Trim();
        if (suggested.Length == 0) suggested = user.Username;
        if (suggested.Length > 150) suggested = suggested[..150];
        user.UpdateDisplayName(suggested);

        await db.SaveChangesAsync(ct);

        if (shouldCreatePlayerProfile)
        {
            var exists = await db.ProfilsJoueurs.AnyAsync(x => x.UtilisateurId == userId, ct);
            if (!exists)
            {
                // Keycloak usernames may contain characters not allowed in the public nickname.
                var candidate = (username ?? "").Trim();
                if (candidate.Length is < 3 or > 32 ||
                    !candidate.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
                    candidate = "player_" + userId.ToString("N")[..10];
                db.ProfilsJoueurs.Add(ProfilJoueur.Create(userId, candidate));
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException ex) when
                    (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    // Concurrent first logins race to provision the same optional profile.
                    // Unique FK is the last-line defense; the winner's profile is authoritative.
                    db.ChangeTracker.Clear();
                }
            }
        }
        return new IdentitySession(user.AuthVersion, user.Status, user.DisplayName);
    }

    public async Task<IdentitySessionState> GetSessionStateAsync(Guid userId, long version, CancellationToken ct)
    {
        var current = await db.Utilisateurs.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Status, u.AuthVersion })
            .SingleOrDefaultAsync(ct);
        if (current is null) return IdentitySessionState.Expired;
        if (current.Status == UserStatus.Suspended) return IdentitySessionState.Suspended;
        return current.AuthVersion == version
            ? IdentitySessionState.Current
            : IdentitySessionState.Expired;
    }

    public async Task<bool> IsSessionCurrentAsync(Guid userId, long version, CancellationToken ct) =>
        await GetSessionStateAsync(userId, version, ct) == IdentitySessionState.Current;

    public async Task<bool> RenamePublicProfileAsync(Guid userId, string nickname, CancellationToken ct)
    {
        var profile = await db.ProfilsJoueurs.SingleOrDefaultAsync(p => p.UtilisateurId == userId, ct);
        if (profile is null) return false;
        profile.Rename(nickname);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
