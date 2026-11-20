
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Administration.Infrastructure.Services;
using QuantDesk.Modules.Infrastructure.Persistence;

namespace QuantDesk.IntegrationTests;

public sealed class UtilisateurProvisioningTests
{
    private static QuantDeskDbContext CreateDbContext()
    {
        var rawConnectionString =
            Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "QD_TEST_CONNECTION_STRING is required for database integration tests.");

        var connection = new NpgsqlConnectionStringBuilder(rawConnectionString);

        // Safety: never run these tests against the application database.
        if (!string.Equals(
                connection.Database,
                "quantdesk_test",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Integration tests must use the quantdesk_test database.");
        }

        var options = new DbContextOptionsBuilder<QuantDeskDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .Options;

        return new QuantDeskDbContext(options);
    }

    private static string NewSubject()
    {
        return $"integration-test-{Guid.NewGuid():N}";
    }

    private static async Task CleanUpAsync(
        QuantDeskDbContext db,
        string subject)
    {
        await db.Utilisateurs
            .Where(user => user.KeycloakSubject == subject)
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task FirstLogin_CreatesUtilisateur()
    {
        var subject = NewSubject();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        try
        {
            var service = new UtilisateurProvisioningService(db);

            var id = await service.ProvisionAsync(
                subject,
                "alice",
                "alice@example.test");

            var user = await db.Utilisateurs
                .AsNoTracking()
                .SingleAsync(u => u.KeycloakSubject == subject);

            Assert.Equal(id, user.Id);
            Assert.Equal(subject, user.KeycloakSubject);
            Assert.Equal("alice", user.Username);
            Assert.Equal("alice@example.test", user.Email);
        }
        finally
        {
            await CleanUpAsync(db, subject);
        }
    }

    [Fact]
    public async Task RepeatedLogin_ReusesSameUtilisateur()
    {
        var subject = NewSubject();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        try
        {
            var service = new UtilisateurProvisioningService(db);

            var firstId = await service.ProvisionAsync(
                subject,
                "alice",
                "alice@example.test");

            var secondId = await service.ProvisionAsync(
                subject,
                "alice",
                "alice@example.test");

            var count = await db.Utilisateurs
                .CountAsync(u => u.KeycloakSubject == subject);

            Assert.Equal(firstId, secondId);
            Assert.Equal(1, count);
        }
        finally
        {
            await CleanUpAsync(db, subject);
        }
    }

    [Fact]
    public async Task ProfileChange_UpdatesExistingUtilisateur()
    {
        var subject = NewSubject();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        try
        {
            var service = new UtilisateurProvisioningService(db);

            var originalId = await service.ProvisionAsync(
                subject,
                "alice",
                "old@example.test");

            var updatedId = await service.ProvisionAsync(
                subject,
                "alice_updated",
                "new@example.test");

            var user = await db.Utilisateurs
                .AsNoTracking()
                .SingleAsync(u => u.KeycloakSubject == subject);

            Assert.Equal(originalId, updatedId);
            Assert.Equal("alice_updated", user.Username);
            Assert.Equal("new@example.test", user.Email);
        }
        finally
        {
            await CleanUpAsync(db, subject);
        }
    }

    [Fact]
    public async Task ConcurrentLogins_CreateOnlyOneUtilisateur()
    {
        var subject = NewSubject();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        try
        {
            // Each concurrent request gets its own DbContext.
            var requests = Enumerable.Range(0, 8)
                .Select(async _ =>
                {
                    await using var requestDb = CreateDbContext();

                    var service =
                        new UtilisateurProvisioningService(requestDb);

                    return await service.ProvisionAsync(
                        subject,
                        "alice",
                        "alice@example.test");
                });

            var ids = await Task.WhenAll(requests);

            Assert.All(ids, id => Assert.Equal(ids[0], id));

            var count = await db.Utilisateurs
                .CountAsync(u => u.KeycloakSubject == subject);

            Assert.Equal(1, count);
        }
        finally
        {
            await CleanUpAsync(db, subject);
        }
    }
}
