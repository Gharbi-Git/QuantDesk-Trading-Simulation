using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class StockPositionPersistenceTests
{
    [Fact]
    public async Task Persisted_stock_position_reloads_with_average_cost_and_prevents_duplicates()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "QD_TEST_CONNECTION_STRING is required for database integration tests.");

        // Guard against accidentally inserting test fixtures into the application database.
        var parsedConnectionString = new NpgsqlConnectionStringBuilder(rawConnectionString);
        if (!string.Equals(parsedConnectionString.Database, "quantdesk_test",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Stock position integration tests may only use the quantdesk_test database.");
        }

        var options = new DbContextOptionsBuilder<QuantDeskDbContext>()
            .UseNpgsql(rawConnectionString)
            .Options;

        var playerId = Guid.NewGuid();
        var uniqueTag = Guid.NewGuid().ToString("N");

        await using (var migrated = new QuantDeskDbContext(options))
        {
            await migrated.Database.MigrateAsync();
        }

        try
        {
            await using (var db = new QuantDeskDbContext(options))
            {
                var subject = "test-position-" + uniqueTag;
                var username = "position_" + uniqueTag;
                var email = "test@example.invalid";
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO utilisateurs (id, keycloak_subject, username, email, created_at_utc) VALUES ({playerId}, {subject}, {username}, {email}, {DateTime.UtcNow})");

                var account = CompteTrading.OpenPersonal(playerId, 100_000m);
                db.ComptesTrading.Add(account);
                await db.SaveChangesAsync();

                var position = PositionAction.OpenWithBuy(account.Id, "AAPL", 10, 100m);
                position.Buy(10, 120m);
                Assert.Equal(100m, position.Sell(5, 130m));

                db.PositionsAction.Add(position);
                await db.SaveChangesAsync();

                await using var reader = new QuantDeskDbContext(options);
                var loaded = await reader.PositionsAction.AsNoTracking()
                    .SingleAsync(p => p.CompteTradingId == account.Id && p.InstrumentId == "AAPL");

                Assert.Equal(15, loaded.Quantity);
                Assert.Equal(1650m, loaded.CostBasis);
                Assert.Equal(110m, loaded.AverageAcquisitionCost);
                Assert.Equal(100m, loaded.RealizedPnl);

                var otherUserId = Guid.NewGuid();
                Assert.Empty(await reader.PositionsAction.AsNoTracking()
                    .Where(p => p.CompteTradingId == account.Id &&
                                p.Portefeuille.CompteTrading.OwnerUserId == otherUserId)
                    .ToListAsync());

                reader.PositionsAction.Add(
                    PositionAction.OpenWithBuy(account.Id, "AAPL", 1, 10m));
                var exception = await Assert.ThrowsAsync<DbUpdateException>(
                    async () => { await reader.SaveChangesAsync(); });
                Assert.IsType<PostgresException>(exception.InnerException);
                Assert.Equal(PostgresErrorCodes.UniqueViolation,
                    ((PostgresException)exception.InnerException!).SqlState);
            }
        }
        finally
        {
            await using var cleanup = new QuantDeskDbContext(options);
            // Position rows, portfolios and journals cascade from their account.
            await cleanup.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM comptes_trading WHERE owner_user_id = {playerId}");
            await cleanup.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM utilisateurs WHERE id = {playerId}");
        }
    }
}
