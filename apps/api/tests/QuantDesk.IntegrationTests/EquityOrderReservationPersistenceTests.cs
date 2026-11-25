using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class EquityOrderReservationPersistenceTests
{
    [Fact]
    public async Task Reservation_round_trips_and_duplicate_order_id_is_rejected()
    {
        var raw = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("QD_TEST_CONNECTION_STRING is required.");
        var parsed = new NpgsqlConnectionStringBuilder(raw);
        if (!string.Equals(parsed.Database, "quantdesk_test", StringComparison.Ordinal))
            throw new InvalidOperationException("Use quantdesk_test only.");
        var options = new DbContextOptionsBuilder<QuantDeskDbContext>().UseNpgsql(raw).Options;
        var ownerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var account = CompteTrading.OpenPersonal(ownerId, 100000m);
        await using (var migration = new QuantDeskDbContext(options))
            await migration.Database.MigrateAsync();
        try
        {
            await using (var db = new QuantDeskDbContext(options))
            {
                var tag = ownerId.ToString("N");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO utilisateurs (id, keycloak_subject, username, email, created_at_utc) VALUES ({ownerId}, {"reservation-" + tag}, {"res_" + tag}, {"test@example.invalid"}, {DateTime.UtcNow})");
                db.ComptesTrading.Add(account);
                await db.SaveChangesAsync();
                db.EquityOrderReservations.Add(EquityOrderReservation.Create(orderId, account.Id, sessionId,
                    "TEST-EQUITY", EquityReservationSide.Buy, 100, 150m));
                await db.SaveChangesAsync();
            }
            await using (var reader = new QuantDeskDbContext(options))
            {
                var loaded = await reader.EquityOrderReservations.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
                Assert.Equal(account.Id, loaded.AccountId);
                Assert.Equal(sessionId, loaded.SessionId);
                Assert.Equal(EquityReservationStatus.Active, loaded.Status);
                Assert.Equal(15000m, loaded.ReservedCash);
                Assert.Equal(100000m, await reader.ComptesTrading.Where(a => a.Id == account.Id).Select(a => a.CashBalance).SingleAsync());
                Assert.Equal(1, await reader.CashJournal.CountAsync(j => j.CompteTradingId == account.Id));
                reader.EquityOrderReservations.Add(EquityOrderReservation.Create(orderId, account.Id, sessionId,
                    "TEST-EQUITY", EquityReservationSide.Buy, 100, 150m));
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => reader.SaveChangesAsync());
                Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
            }
            // Lifecycle mutations are tested on isolated fixtures only. Production
            // fill consumption must eventually share the settlement transaction.
            await using (var writer = new QuantDeskDbContext(options))
            {
                var loaded = await writer.EquityOrderReservations.SingleAsync(r => r.OrderId == orderId);
                loaded.ApplyFill(40, 145m);
                await writer.SaveChangesAsync();
            }
            await using (var reader = new QuantDeskDbContext(options))
            {
                var loaded = await reader.EquityOrderReservations.SingleAsync(r => r.OrderId == orderId);
                Assert.Equal(40L, loaded.FilledQuantity);
                Assert.Equal(9000m, loaded.ReservedCash);
                Assert.Equal(1L, loaded.Version);
                loaded.Release();
                await reader.SaveChangesAsync();
            }
            await using (var reader = new QuantDeskDbContext(options))
            {
                var loaded = await reader.EquityOrderReservations.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
                Assert.Equal(EquityReservationStatus.Released, loaded.Status);
                Assert.Equal(40L, loaded.FilledQuantity);
                Assert.Equal(0m, loaded.ReservedCash);
                Assert.Equal(2L, loaded.Version);
                Assert.Equal(100000m, await reader.ComptesTrading.Where(a => a.Id == account.Id).Select(a => a.CashBalance).SingleAsync());
                Assert.Equal(1, await reader.CashJournal.CountAsync(j => j.CompteTradingId == account.Id));
            }
        }
        finally
        {
            await using var cleanup = new QuantDeskDbContext(options);
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM equity_order_reservations WHERE account_id = {account.Id}");
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM comptes_trading WHERE owner_user_id = {ownerId}");
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM utilisateurs WHERE id = {ownerId}");
        }
    }
}
