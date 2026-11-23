using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Administration.Domain.Entities;
using QuantDesk.Modules.Administration.Infrastructure.Services;
using QuantDesk.Modules.Infrastructure.Persistence;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class IdentityLifecycleTests
{
    [Fact] public void Public_nickname_rejects_unsafe_characters()
    {
        Assert.Throws<ArgumentException>(() => ProfilJoueur.Create(Guid.NewGuid(), "<script>"));
        Assert.Throws<ArgumentException>(() => ProfilJoueur.Create(Guid.NewGuid(), "a"));
    }

    [Fact] public void Player_profile_can_be_renamed()
    {
        var p = ProfilJoueur.Create(Guid.NewGuid(), "student_001");
        p.Rename("Trader.Pro");
        Assert.Equal("Trader.Pro", p.PseudoPublic);
    }

    [Fact] public async Task Database_profile_is_unique_and_suspension_revokes_existing_session()
    {
        var connectionString = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("QD_TEST_CONNECTION_STRING is required for database integration tests.");
        var options = new DbContextOptionsBuilder<QuantDeskDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new QuantDeskDbContext(options);
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        var subject = Guid.NewGuid().ToString("D");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO utilisateurs (id, keycloak_subject, username, email, created_at_utc) VALUES ({id}, {subject}, {"player_probe"}, {"probe@example.test"}, {DateTime.UtcNow})");
            db.ChangeTracker.Clear();
            var service = new UserIdentityLifecycleService(db);
            var session = await service.CompleteLoginAsync(id, "Player Test", "player_probe", true, CancellationToken.None);
            Assert.Equal(UserStatus.Active, session.Status);
            Assert.True(await service.IsSessionCurrentAsync(id, session.AuthVersion, CancellationToken.None));
            Assert.Single(await db.ProfilsJoueurs.Where(x => x.UtilisateurId == id).ToListAsync());
            // Repeat login must re-use the same optional profile.
            await service.CompleteLoginAsync(id, "Player Test", "player_probe", true, CancellationToken.None);
            Assert.Single(await db.ProfilsJoueurs.Where(x => x.UtilisateurId == id).ToListAsync());

            var user = await db.Utilisateurs.SingleAsync(x => x.Id == id);
            user.Suspendre();
            await db.SaveChangesAsync();
            Assert.False(await service.IsSessionCurrentAsync(id, session.AuthVersion, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteLoginAsync(id, null, null, true, CancellationToken.None));
            user.Activer();
            await db.SaveChangesAsync();
            Assert.False(await service.IsSessionCurrentAsync(id, session.AuthVersion, CancellationToken.None));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM utilisateurs WHERE id = {id}");
        }
    }
}
