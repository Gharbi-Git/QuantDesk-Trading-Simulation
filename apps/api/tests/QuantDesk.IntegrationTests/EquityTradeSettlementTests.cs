using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class EquityTradeSettlementTests
{
    [Fact]
    public async Task Fill_moves_exact_cash_and_shares_and_writes_two_journal_entries()
    {
        await using var f = await Fixture.CreateAsync();
        var execution = f.Execution(f.BuyerIds[0], 4, 125m);
        var result = await f.SettleAsync(execution);
        Assert.False(result.AlreadySettled);

        await using var db = f.OpenDb();
        var buyer = await db.ComptesTrading.AsNoTracking().SingleAsync(a => a.Id == f.BuyerIds[0]);
        var seller = await db.ComptesTrading.AsNoTracking().SingleAsync(a => a.Id == f.SellerId);
        Assert.Equal(99_500m, buyer.CashBalance);
        Assert.Equal(100_500m, seller.CashBalance);
        Assert.Equal(200_000m, buyer.CashBalance + seller.CashBalance);
        Assert.Equal(1L, buyer.Version);
        Assert.Equal(1L, seller.Version);

        var holding = await db.PositionsAction.AsNoTracking()
            .SingleAsync(p => p.CompteTradingId == f.BuyerIds[0]);
        var remainder = await db.PositionsAction.AsNoTracking()
            .SingleAsync(p => p.CompteTradingId == f.SellerId);
        Assert.Equal(4L, holding.Quantity);
        Assert.Equal(500m, holding.CostBasis);
        Assert.Equal(6L, remainder.Quantity);
        Assert.Equal(600m, remainder.CostBasis);
        Assert.Equal(100m, remainder.RealizedPnl);
        Assert.Equal(10L, holding.Quantity + remainder.Quantity);

        var tradeJournal = await db.CashJournal.AsNoTracking()
            .Where(j => j.EventKey.StartsWith("stock-fill:" + execution.ExecutionId.ToString("D")))
            .ToListAsync();
        Assert.Equal(2, tradeJournal.Count);
        Assert.Equal(0m, tradeJournal.Sum(j => j.Amount));
        Assert.Equal(1, await db.EquitySettlements.CountAsync(s => s.ExecutionId == execution.ExecutionId));
    }

    [Fact]
    public async Task Repeating_the_same_execution_does_not_double_debit_or_duplicate_journal()
    {
        await using var f = await Fixture.CreateAsync();
        var execution = f.Execution(f.BuyerIds[0], 3, 100m);
        Assert.False((await f.SettleAsync(execution)).AlreadySettled);
        Assert.True((await f.SettleAsync(execution)).AlreadySettled);

        await using var db = f.OpenDb();
        Assert.Equal(99_700m, await db.ComptesTrading.Where(a => a.Id == f.BuyerIds[0])
            .Select(a => a.CashBalance).SingleAsync());
        Assert.Equal(1, await db.EquitySettlements.CountAsync(s => s.InstrumentId == f.InstrumentId));
        Assert.Equal(2, await db.CashJournal.CountAsync(j =>
            j.EventKey.StartsWith("stock-fill:" + execution.ExecutionId.ToString("D"))));
    }

    [Fact]
    public async Task Reused_execution_id_with_changed_details_is_rejected()
    {
        await using var f = await Fixture.CreateAsync();
        var execution = f.Execution(f.BuyerIds[0], 1, 110m);
        await f.SettleAsync(execution);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.SettleAsync(execution with { Quantity = 2 }));
    }

    [Fact]
    public async Task Insufficient_cash_rolls_back_both_positions_and_journals()
    {
        await using var f = await Fixture.CreateAsync();
        var execution = f.Execution(f.BuyerIds[0], 5, 25_000m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.SettleAsync(execution));

        await using var db = f.OpenDb();
        Assert.Equal(100_000m, await db.ComptesTrading.Where(a => a.Id == f.BuyerIds[0])
            .Select(a => a.CashBalance).SingleAsync());
        Assert.Equal(10L, await db.PositionsAction.Where(p => p.CompteTradingId == f.SellerId)
            .Select(p => p.Quantity).SingleAsync());
        Assert.False(await db.EquitySettlements.AnyAsync(s => s.InstrumentId == f.InstrumentId));
        Assert.Equal(2, await db.CashJournal.CountAsync(j => j.CompteTradingId == f.SellerId || f.BuyerIds.Contains(j.CompteTradingId))); // initial funding only
    }

    [Fact]
    public async Task Overselling_fails_without_creating_cash_or_shares()
    {
        await using var f = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.SettleAsync(f.Execution(f.BuyerIds[0], 11, 100m)));
        await using var db = f.OpenDb();
        Assert.False(await db.EquitySettlements.AnyAsync(s => s.InstrumentId == f.InstrumentId));
        Assert.Equal(10L, await db.PositionsAction.Where(p => p.CompteTradingId == f.SellerId)
            .Select(p => p.Quantity).SingleAsync());
        Assert.Equal(2, await db.CashJournal.CountAsync(j => j.CompteTradingId == f.SellerId || f.BuyerIds.Contains(j.CompteTradingId)));
    }

    [Fact]
    public async Task Two_competing_buyers_cannot_both_purchase_the_last_shares()
    {
        await using var f = await Fixture.CreateAsync(buyerCount: 2);
        var a = f.Execution(f.BuyerIds[0], 8, 125m);
        var b = f.Execution(f.BuyerIds[1], 8, 125m);
        var results = await Task.WhenAll(AttemptAsync(f, a), AttemptAsync(f, b));
        Assert.Single(results, x => x is null);
        Assert.Single(results, x => x is InvalidOperationException);

        await using var db = f.OpenDb();
        Assert.Equal(1, await db.EquitySettlements.CountAsync(s => s.InstrumentId == f.InstrumentId));
        var held = await db.PositionsAction.Where(p => f.BuyerIds.Contains(p.CompteTradingId))
            .SumAsync(p => p.Quantity);
        var sellerHeld = await db.PositionsAction.Where(p => p.CompteTradingId == f.SellerId)
            .Select(p => p.Quantity).SingleAsync();
        Assert.Equal(10L, held + sellerHeld);
        Assert.Equal(300_000m, await db.ComptesTrading.Where(a => a.Id == f.SellerId || f.BuyerIds.Contains(a.Id)).SumAsync(a => a.CashBalance));
    }

    [Fact]
    public async Task Two_concurrent_replays_are_idempotent()
    {
        await using var f = await Fixture.CreateAsync();
        var execution = f.Execution(f.BuyerIds[0], 4, 100m);
        var results = await Task.WhenAll(f.SettleAsync(execution), f.SettleAsync(execution));
        Assert.Single(results, r => !r.AlreadySettled);
        Assert.Single(results, r => r.AlreadySettled);

        await using var db = f.OpenDb();
        Assert.Equal(1, await db.EquitySettlements.CountAsync(s => s.InstrumentId == f.InstrumentId));
        Assert.Equal(2, await db.CashJournal.CountAsync(j =>
            j.EventKey.StartsWith("stock-fill:" + execution.ExecutionId.ToString("D"))));
    }

    [Fact]
    public async Task Stale_position_update_throws_optimistic_concurrency_error()
    {
        await using var f = await Fixture.CreateAsync();
        await using var contextA = f.OpenDb();
        await using var contextB = f.OpenDb();
        var a = await contextA.PositionsAction.SingleAsync(p => p.CompteTradingId == f.SellerId);
        var b = await contextB.PositionsAction.SingleAsync(p => p.CompteTradingId == f.SellerId);
        b.Buy(1, 100m);
        await contextB.SaveChangesAsync();
        a.Buy(1, 100m);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextA.SaveChangesAsync());
    }

    private static async Task<Exception?> AttemptAsync(Fixture fixture, TrustedEquityExecution execution)
    {
        try { await fixture.SettleAsync(execution); return null; }
        catch (Exception ex) { return ex; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<QuantDeskDbContext> _options;
        private readonly List<Guid> _playerIds = new();
        private readonly List<Guid> _accountIds = new();
        public Guid SellerId { get; private set; }
        public List<Guid> BuyerIds { get; } = new();
        public string InstrumentId { get; } = "EQ-" + Guid.NewGuid().ToString("N");

        private Fixture(DbContextOptions<QuantDeskDbContext> options) => _options = options;
        public QuantDeskDbContext OpenDb() => new(_options);

        public static async Task<Fixture> CreateAsync(int buyerCount = 1)
        {
            var cs = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING") ??
                throw new InvalidOperationException("QD_TEST_CONNECTION_STRING is required.");
            if (!string.Equals(new NpgsqlConnectionStringBuilder(cs).Database,
                    "quantdesk_test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Tests must only use quantdesk_test.");

            var options = new DbContextOptionsBuilder<QuantDeskDbContext>()
                .UseNpgsql(cs).Options;
            await using (var migrated = new QuantDeskDbContext(options))
                await migrated.Database.MigrateAsync();

            var f = new Fixture(options);
            await using var db = f.OpenDb();
            for (int i = 0; i <= buyerCount; i++)
            {
                var userId = Guid.NewGuid();
                var subject = "settlement-" + userId.ToString("N");
                var username = "settle_" + userId.ToString("N");
                var email = "test@example.invalid";
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO utilisateurs (id, keycloak_subject, username, email, created_at_utc) VALUES ({userId}, {subject}, {username}, {email}, {DateTime.UtcNow})");
                f._playerIds.Add(userId);
                var account = CompteTrading.OpenPersonal(userId, 100_000m);
                db.ComptesTrading.Add(account);
                f._accountIds.Add(account.Id);
                if (i == 0) f.SellerId = account.Id;
                else f.BuyerIds.Add(account.Id);
            }
            await db.SaveChangesAsync();
            db.PositionsAction.Add(PositionAction.OpenWithBuy(f.SellerId, f.InstrumentId, 10, 100m));
            await db.SaveChangesAsync();
            return f;
        }

        public TrustedEquityExecution Execution(Guid buyerId, long quantity, decimal price) =>
            new(Guid.NewGuid(), buyerId, SellerId, InstrumentId, quantity, price);

        public async Task<EquitySettlementResult> SettleAsync(TrustedEquityExecution execution)
        {
            await using var db = OpenDb();
            return await new EquityTradeSettlementService(db).SettleTrustedFillAsync(execution);
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = OpenDb();
            // Settlement has RESTRICT FKs to both accounts, so remove receipts first.
            foreach (var accountId in _accountIds)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM equity_trade_settlements WHERE buyer_account_id = {accountId} OR seller_account_id = {accountId}");
            foreach (var accountId in _accountIds)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM comptes_trading WHERE \"Id\" = {accountId}");
            foreach (var userId in _playerIds)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM utilisateurs WHERE id = {userId}");
        }
    }
}





