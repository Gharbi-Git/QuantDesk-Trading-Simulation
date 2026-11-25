using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class PortfolioReservationServiceTests
{
    [Fact]
    public async Task Buy_reserves_cash_without_journal_or_balance_mutation_and_replay_is_idempotent()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        var result = await f.Buy(id, 100, 150m);
        Assert.Equal(15000m, result.ReservedCash);
        Assert.False(result.AlreadyApplied);
        Assert.True((await f.Buy(id, 100, 150m)).AlreadyApplied);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Buy(id, 101, 150m));
        await using var db = f.OpenDb();
        var account = await db.ComptesTrading.SingleAsync(a => a.Id == f.Buyer.Id);
        Assert.Equal(100000m, account.CashBalance);
        Assert.Equal(1L, account.Version);
        Assert.Equal(1, await db.CashJournal.CountAsync(j => j.CompteTradingId == account.Id));
        Assert.Equal(1, await db.EquityOrderReservations.CountAsync(r => r.AccountId == account.Id));
    }

    [Fact]
    public async Task Buy_overreservation_is_rejected_across_sessions_and_instruments()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Buy(Guid.NewGuid(), 600, 100m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReserveAsync(f.Buyer.OwnerUserId,
            Guid.NewGuid(), f.Buyer.Id, Guid.NewGuid(), "OTHER-TEST-INSTRUMENT", EquityReservationSide.Buy, 401, 100m));
        await using var db = f.OpenDb();
        Assert.Equal(1, await db.EquityOrderReservations.CountAsync(r => r.AccountId == f.Buyer.Id));
    }

    [Fact]
    public async Task Sell_reservation_cannot_exceed_owned_available_shares()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(7L, (await f.Sell(Guid.NewGuid(), 7)).ReservedShares);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sell(Guid.NewGuid(), 4));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReserveAsync(f.Seller.OwnerUserId,
            Guid.NewGuid(), f.Seller.Id, f.SessionId, "UNOWNED-TEST-INSTRUMENT", EquityReservationSide.Sell, 1, 100m));
        await using var db = f.OpenDb();
        Assert.Equal(10L, await db.PositionsAction.Where(p => p.CompteTradingId == f.Seller.Id).Select(p => p.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Confirmed_release_is_idempotent_and_restores_buying_power()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.Buy(id, 1000, 100m);
        var released = await f.Release(id);
        Assert.Equal(EquityReservationStatus.Released, released.Status);
        Assert.Equal(0m, released.ReservedCash);
        Assert.True((await f.Release(id)).AlreadyApplied);
        Assert.Equal(EquityReservationStatus.Released, (await f.Buy(id, 1000, 100m)).Status);
        await f.Buy(Guid.NewGuid(), 1000, 100m);
        await using var db = f.OpenDb();
        Assert.Equal(3L, await db.ComptesTrading.Where(a => a.Id == f.Buyer.Id).Select(a => a.Version).SingleAsync());
        Assert.Equal(1, await db.CashJournal.CountAsync(j => j.CompteTradingId == f.Buyer.Id));
    }

    [Fact]
    public async Task Wrong_owner_session_or_account_cannot_mutate_reservation()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.Buy(id, 100, 150m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReserveAsync(f.Seller.OwnerUserId,
            Guid.NewGuid(), f.Buyer.Id, f.SessionId, f.InstrumentId, EquityReservationSide.Buy, 1, 100m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReleaseConfirmedAsync(f.Seller.OwnerUserId, f.Buyer.Id, f.SessionId, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReleaseConfirmedAsync(f.Buyer.OwnerUserId, f.Buyer.Id, Guid.NewGuid(), id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReleaseConfirmedAsync(f.Seller.OwnerUserId, f.Seller.Id, f.SessionId, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReserveAsync(f.Seller.OwnerUserId, id,
            f.Seller.Id, f.SessionId, f.InstrumentId, EquityReservationSide.Sell, 1, 100m));
        await using var db = f.OpenDb();
        Assert.Equal(EquityReservationStatus.Active, (await db.EquityOrderReservations.SingleAsync(r => r.OrderId == id)).Status);
    }

    [Fact]
    public async Task Concurrent_buys_cannot_reserve_the_same_cash()
    {
        await using var f = await Fixture.CreateAsync();
        var results = await Task.WhenAll(Attempt(() => f.Buy(Guid.NewGuid(), 600, 100m)), Attempt(() => f.Buy(Guid.NewGuid(), 600, 100m)));
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is InvalidOperationException);
        await using var db = f.OpenDb();
        Assert.Equal(1, await db.EquityOrderReservations.CountAsync(r => r.AccountId == f.Buyer.Id));
    }

    [Fact]
    public async Task Concurrent_sells_cannot_reserve_the_same_shares()
    {
        await using var f = await Fixture.CreateAsync();
        var results = await Task.WhenAll(Attempt(() => f.Sell(Guid.NewGuid(), 7)), Attempt(() => f.Sell(Guid.NewGuid(), 7)));
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is InvalidOperationException);
        await using var db = f.OpenDb();
        Assert.Equal(1, await db.EquityOrderReservations.CountAsync(r => r.AccountId == f.Seller.Id));
    }

    [Fact]
    public async Task Legacy_settlement_cannot_spend_reserved_cash_or_shares()
    {
        await using var f = await Fixture.CreateAsync();
        var buyId = Guid.NewGuid();
        await f.Buy(buyId, 1000, 100m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Settle(1, 100m));
        await f.Release(buyId);
        await f.Sell(Guid.NewGuid(), 10);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Settle(1, 100m));
        await using var db = f.OpenDb();
        Assert.Equal(100000m, await db.ComptesTrading.Where(a => a.Id == f.Buyer.Id).Select(a => a.CashBalance).SingleAsync());
        Assert.Equal(10L, await db.PositionsAction.Where(p => p.CompteTradingId == f.Seller.Id).Select(p => p.Quantity).SingleAsync());
        Assert.Equal(2, await db.CashJournal.CountAsync(j => j.CompteTradingId == f.Buyer.Id || j.CompteTradingId == f.Seller.Id));
        Assert.False(await db.EquitySettlements.AnyAsync(s => s.BuyerAccountId == f.Buyer.Id));
    }

    [Fact]
    public async Task Concurrent_legacy_settlement_and_reserve_cannot_overspend_cash()
    {
        await using var f = await Fixture.CreateAsync();
        async Task<Exception?> SettleAttempt()
        {
            try { await f.Settle(1, 50000m); return null; }
            catch (Exception ex) { return ex; }
        }
        var results = await Task.WhenAll(Attempt(() => f.Buy(Guid.NewGuid(), 600, 100m)), SettleAttempt());
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is InvalidOperationException);
        await using var db = f.OpenDb();
        var cash = await db.ComptesTrading.Where(a => a.Id == f.Buyer.Id).Select(a => a.CashBalance).SingleAsync();
        var reservations = await db.EquityOrderReservations.Where(r => r.AccountId == f.Buyer.Id).ToListAsync();
        Assert.True(cash >= reservations.Sum(r => r.ReservedCash));
    }

    [Fact]
    public async Task Linked_partial_and_final_fills_consume_both_reservations_and_post_once()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        Assert.False((await f.Linked(fill)).AlreadySettled);
        await using (var db = f.OpenDb())
        {
            var buy = await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.BuyOrderId);
            var sell = await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.SellOrderId);
            Assert.Equal(6L, buy.RemainingQuantity);
            Assert.Equal(900m, buy.ReservedCash);
            Assert.Equal(6L, sell.ReservedShares);
            Assert.Equal(99420m, await db.ComptesTrading.Where(a => a.Id == f.Buyer.Id).Select(a => a.CashBalance).SingleAsync());
            Assert.Equal(100580m, await db.ComptesTrading.Where(a => a.Id == f.Seller.Id).Select(a => a.CashBalance).SingleAsync());
            Assert.Equal(4L, await db.PositionsAction.Where(p => p.CompteTradingId == f.Buyer.Id).Select(p => p.Quantity).SingleAsync());
            var receipt = await db.EquitySettlements.SingleAsync(e => e.ExecutionId == fill.ExecutionId);
            Assert.Equal<Guid?>(fill.BuyOrderId, receipt.BuyOrderId);
            Assert.Equal<Guid?>(fill.SellOrderId, receipt.SellOrderId);
            Assert.Equal<Guid?>(fill.SessionId, receipt.SessionId);
        }
        var final = fill with { ExecutionId = Guid.NewGuid(), Quantity = 6, ExecutionPrice = 150m };
        Assert.False((await f.Linked(final)).AlreadySettled);
        Assert.True((await f.Linked(final)).AlreadySettled);
        await using var check = f.OpenDb();
        var reservations = await check.EquityOrderReservations.Where(r => r.OrderId == fill.BuyOrderId || r.OrderId == fill.SellOrderId).ToListAsync();
        Assert.All(reservations, r => { Assert.Equal(EquityReservationStatus.Filled, r.Status); Assert.Equal(0L, r.RemainingQuantity); });
        Assert.Equal(98520m, await check.ComptesTrading.Where(a => a.Id == f.Buyer.Id).Select(a => a.CashBalance).SingleAsync());
        Assert.Equal(10L, await check.PositionsAction.Where(p => p.CompteTradingId == f.Buyer.Id).Select(p => p.Quantity).SingleAsync());
        Assert.Equal(0L, await check.PositionsAction.Where(p => p.CompteTradingId == f.Seller.Id).Select(p => p.Quantity).SingleAsync());
        Assert.Equal(6, await check.CashJournal.CountAsync(j => j.CompteTradingId == f.Buyer.Id || j.CompteTradingId == f.Seller.Id));
        Assert.Equal(2, await check.EquitySettlements.CountAsync(e => e.BuyerAccountId == f.Buyer.Id));
    }

    [Fact]
    public async Task Linked_fill_replay_checks_order_identity_and_session_even_after_release()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        await f.Linked(fill);
        await f.Release(fill.BuyOrderId);
        Assert.True((await f.Linked(fill)).AlreadySettled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Linked(fill with { BuyOrderId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Linked(fill with { SessionId = Guid.NewGuid() }));
        await using var db = f.OpenDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EquityTradeSettlementService(db).SettleTrustedFillAsync(
            new TrustedEquityExecution(fill.ExecutionId, fill.BuyerAccountId, fill.SellerAccountId, fill.InstrumentId, fill.Quantity, fill.ExecutionPrice)));
        Assert.Equal(1, await db.EquitySettlements.CountAsync(e => e.BuyerAccountId == f.Buyer.Id));
    }

    [Fact]
    public async Task Linked_fill_rejects_wrong_session_instrument_side_account_and_missing_order()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        foreach (var invalid in new[] {
            fill with { SessionId = Guid.NewGuid() },
            fill with { InstrumentId = "WRONG" },
            fill with { BuyOrderId = fill.SellOrderId, SellOrderId = fill.BuyOrderId },
            fill with { BuyerAccountId = f.Seller.Id, SellerAccountId = f.Buyer.Id },
            fill with { SellOrderId = Guid.NewGuid() } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Linked(invalid));
        await AssertUntouched(f, fill);
    }

    [Fact]
    public async Task Linked_fill_rejects_overfill_and_both_limit_price_violations()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        foreach (var invalid in new[] { fill with { Quantity = 11 }, fill with { ExecutionPrice = 151m }, fill with { ExecutionPrice = 99m } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Linked(invalid));
        await AssertUntouched(f, fill);
    }

    [Fact]
    public async Task Linked_fill_preserves_other_reserved_cash_and_releases_only_unused_after_partial_fill()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        var other = Guid.NewGuid();
        await f.Buy(other, 985, 100m); // reserves the rest of the 100k balance
        await f.Linked(fill);
        await f.Release(fill.BuyOrderId);
        await using var db = f.OpenDb();
        var buy = await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.BuyOrderId);
        Assert.Equal(4L, buy.FilledQuantity);
        Assert.Equal(0m, buy.ReservedCash);
        Assert.Equal(98500m, (await db.EquityOrderReservations.SingleAsync(r => r.OrderId == other)).ReservedCash);
        Assert.Equal(920m, (await db.ComptesTrading.SingleAsync(a => a.Id == f.Buyer.Id)).CashBalance - 98500m);
    }

    [Fact]
    public async Task Concurrent_linked_execution_replay_has_one_financial_effect()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        var results = await Task.WhenAll(f.Linked(fill), f.Linked(fill));
        Assert.Single(results, r => r.AlreadySettled);
        Assert.Single(results, r => !r.AlreadySettled);
        await using var db = f.OpenDb();
        Assert.Equal(4L, (await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.BuyOrderId)).FilledQuantity);
        Assert.Equal(1, await db.EquitySettlements.CountAsync(e => e.BuyerAccountId == f.Buyer.Id));
        Assert.Equal(4, await db.CashJournal.CountAsync(j => j.CompteTradingId == f.Buyer.Id || j.CompteTradingId == f.Seller.Id));
    }

    [Fact]
    public async Task Released_order_rejects_new_execution_and_does_not_consume_other_side()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        await f.Release(fill.BuyOrderId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Linked(fill));
        await AssertUntouched(f, fill);
    }

    [Fact]
    public async Task Linked_database_failure_rolls_back_reservations_cash_positions_and_receipt()
    {
        await using var f = await Fixture.CreateAsync();
        var fill = await PrepareLinked(f);
        // Deliberately saturate the seller's numeric(20,4) balance in the test DB.
        // A positive settlement credit then fails during SaveChanges, after staging mutations.
        const decimal maxCash = 9999999999999999.9999m;
        await using (var setup = f.OpenDb())
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE comptes_trading SET cash_balance = {maxCash} WHERE \"Id\" = {f.Seller.Id}");
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Linked(fill));
        await using var db = f.OpenDb();
        Assert.Equal(100000m, (await db.ComptesTrading.SingleAsync(a => a.Id == f.Buyer.Id)).CashBalance);
        Assert.Equal(maxCash, (await db.ComptesTrading.SingleAsync(a => a.Id == f.Seller.Id)).CashBalance);
        Assert.Equal(0L, (await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.BuyOrderId)).FilledQuantity);
        Assert.Equal(0L, (await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.SellOrderId)).FilledQuantity);
        Assert.Equal(2, await db.CashJournal.CountAsync(j => j.CompteTradingId == f.Buyer.Id || j.CompteTradingId == f.Seller.Id));
        Assert.False(await db.EquitySettlements.AnyAsync(e => e.BuyerAccountId == f.Buyer.Id));
        Assert.Equal(10L, (await db.PositionsAction.SingleAsync(p => p.CompteTradingId == f.Seller.Id)).Quantity);
        Assert.False(await db.PositionsAction.AnyAsync(p => p.CompteTradingId == f.Buyer.Id));
    }

    private static async Task<TrustedReservedEquityExecution> PrepareLinked(Fixture f)
    {
        var buy = Guid.NewGuid(); var sell = Guid.NewGuid();
        await f.Buy(buy, 10, 150m); await f.Sell(sell, 10);
        return new TrustedReservedEquityExecution(Guid.NewGuid(), f.Buyer.Id, f.Seller.Id,
            f.InstrumentId, 4, 145m, buy, sell, f.SessionId);
    }

    private static async Task AssertUntouched(Fixture f, TrustedReservedEquityExecution fill)
    {
        await using var db = f.OpenDb();
        Assert.Equal(100000m, (await db.ComptesTrading.SingleAsync(a => a.Id == f.Buyer.Id)).CashBalance);
        Assert.Equal(100000m, (await db.ComptesTrading.SingleAsync(a => a.Id == f.Seller.Id)).CashBalance);
        Assert.Equal(0L, (await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.BuyOrderId)).FilledQuantity);
        Assert.Equal(0L, (await db.EquityOrderReservations.SingleAsync(r => r.OrderId == fill.SellOrderId)).FilledQuantity);
        Assert.Equal(2, await db.CashJournal.CountAsync(j => j.CompteTradingId == f.Buyer.Id || j.CompteTradingId == f.Seller.Id));
        Assert.False(await db.EquitySettlements.AnyAsync(e => e.BuyerAccountId == f.Buyer.Id));
        Assert.Equal(10L, (await db.PositionsAction.SingleAsync(p => p.CompteTradingId == f.Seller.Id)).Quantity);
        Assert.False(await db.PositionsAction.AnyAsync(p => p.CompteTradingId == f.Buyer.Id));
    }

    private static async Task<Exception?> Attempt(Func<Task<ReservationOutcome>> run)
    {
        try { await run(); return null; }
        catch (Exception ex) { return ex; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<QuantDeskDbContext> _options;
        public CompteTrading Buyer { get; }
        public CompteTrading Seller { get; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public string InstrumentId { get; } = "EQ-" + Guid.NewGuid().ToString("N");
        public PortfolioReservationService Service => new(_options);
        private Fixture(DbContextOptions<QuantDeskDbContext> options)
        {
            _options = options;
            Buyer = CompteTrading.OpenPersonal(Guid.NewGuid(), 100000m);
            Seller = CompteTrading.OpenPersonal(Guid.NewGuid(), 100000m);
        }
        public QuantDeskDbContext OpenDb() => new(_options);
        public Task<EquitySettlementResult> Linked(TrustedReservedEquityExecution fill) => new ReservedEquitySettlementService(_options).SettleAsync(fill);
        public static async Task<Fixture> CreateAsync()
        {
            var raw = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("QD_TEST_CONNECTION_STRING is required.");
            if (!string.Equals(new NpgsqlConnectionStringBuilder(raw).Database, "quantdesk_test", StringComparison.Ordinal))
                throw new InvalidOperationException("Use quantdesk_test only.");
            var options = new DbContextOptionsBuilder<QuantDeskDbContext>().UseNpgsql(raw).Options;
            var f = new Fixture(options);
            await using var db = f.OpenDb();
            await db.Database.MigrateAsync();
            try
            {
                foreach (var a in new[] { f.Buyer, f.Seller })
                {
                    var id = a.OwnerUserId;
                    var tag = id.ToString("N");
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO utilisateurs (id, keycloak_subject, username, email, created_at_utc) VALUES ({id}, {"res-service-" + tag}, {"rs_" + tag}, {"test@example.invalid"}, {DateTime.UtcNow})");
                    db.ComptesTrading.Add(a);
                }
                await db.SaveChangesAsync();
                db.PositionsAction.Add(PositionAction.OpenWithBuy(f.Seller.Id, f.InstrumentId, 10, 100m));
                await db.SaveChangesAsync();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public Task<ReservationOutcome> Buy(Guid id, long qty, decimal price) => Service.ReserveAsync(Buyer.OwnerUserId, id, Buyer.Id, SessionId, InstrumentId, EquityReservationSide.Buy, qty, price);
        public Task<ReservationOutcome> Sell(Guid id, long qty) => Service.ReserveAsync(Seller.OwnerUserId, id, Seller.Id, SessionId, InstrumentId, EquityReservationSide.Sell, qty, 100m);
        public Task<ReservationOutcome> Release(Guid id) => Service.ReleaseConfirmedAsync(Buyer.OwnerUserId, Buyer.Id, SessionId, id);
        public async Task<EquitySettlementResult> Settle(long qty, decimal price)
        {
            await using var db = OpenDb();
            return await new EquityTradeSettlementService(db).SettleTrustedFillAsync(new TrustedEquityExecution(Guid.NewGuid(), Buyer.Id, Seller.Id, InstrumentId, qty, price));
        }
        public async ValueTask DisposeAsync()
        {
            await using var db = OpenDb();
            foreach (var a in new[] { Buyer, Seller })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM equity_trade_settlements WHERE buyer_account_id = {a.Id} OR seller_account_id = {a.Id}");
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM equity_order_reservations WHERE account_id = {a.Id}");
            }
            foreach (var a in new[] { Buyer, Seller })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM comptes_trading WHERE \"Id\" = {a.Id}");
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM utilisateurs WHERE id = {a.OwnerUserId}");
            }
        }
    }
}
