using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class PortfolioSnapshotReaderTests
{
    [Fact]
    public async Task Cash_only_snapshot_has_full_nav_and_no_reservations()
    {
        await using var f = await Fixture.CreateAsync();
        var s = (await f.Reader.ReadOwnedAsync(f.Buyer.OwnerUserId, f.Buyer.Id))!;
        Assert.Equal(100000m, s.CashBalance);
        Assert.Equal(100000m, s.AvailableCash);
        Assert.Equal(0m, s.ReservedCash);
        Assert.Equal(100000m, s.Nav);
        Assert.Equal(0L, s.Version);
        Assert.Empty(s.Positions);
        Assert.Equal(DateTimeKind.Utc, s.AsOfUtc.Kind);
    }

    [Fact]
    public async Task Active_buys_aggregate_across_sessions_and_release_advances_version()
    {
        await using var f = await Fixture.CreateAsync();
        var order = Guid.NewGuid();
        await f.Buy(order, 100, 150m);
        await f.Service.ReserveAsync(f.Buyer.OwnerUserId, Guid.NewGuid(), f.Buyer.Id,
            Guid.NewGuid(), "OTHER-TEST-INSTRUMENT", EquityReservationSide.Buy, 20, 100m);
        var s = (await f.Reader.ReadOwnedAsync(f.Buyer.OwnerUserId, f.Buyer.Id))!;
        Assert.Equal(17000m, s.ReservedCash);
        Assert.Equal(83000m, s.AvailableCash);
        Assert.Equal(100000m, s.Nav);
        Assert.Equal(2L, s.Version);
        await f.Release(order);
        await f.Release(order);
        var after = (await f.Reader.ReadOwnedAsync(f.Buyer.OwnerUserId, f.Buyer.Id))!;
        Assert.Equal(2000m, after.ReservedCash);
        Assert.Equal(98000m, after.AvailableCash);
        Assert.Equal(3L, after.Version);
    }

    [Fact]
    public async Task Stock_reservation_does_not_change_owned_quantity_or_invent_a_mark()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Sell(Guid.NewGuid(), 7);
        var s = (await f.Reader.ReadOwnedAsync(f.Seller.OwnerUserId, f.Seller.Id))!;
        var row = Assert.Single(s.Positions);
        Assert.Equal(10L, row.Quantity);
        Assert.Equal(7L, row.ReservedShares);
        Assert.Equal(3L, row.AvailableShares);
        Assert.Null(row.MarkPrice);
        Assert.Null(s.Nav);
        Assert.Equal(f.InstrumentId, Assert.Single(s.MissingMarkInstruments));
        Assert.Equal(1L, s.Version);
    }

    [Fact]
    public async Task Linked_partial_fill_changes_both_snapshots_once_and_releases_price_improvement()
    {
        await using var f = await Fixture.CreateAsync();
        var buy = Guid.NewGuid(); var sell = Guid.NewGuid();
        await f.Buy(buy, 10, 150m);
        await f.Sell(sell, 10);
        var fill = new TrustedReservedEquityExecution(Guid.NewGuid(), f.Buyer.Id, f.Seller.Id,
            f.InstrumentId, 4, 145m, buy, sell, f.SessionId);
        Assert.False((await f.Settlement.SettleAsync(fill)).AlreadySettled);
        Assert.True((await f.Settlement.SettleAsync(fill)).AlreadySettled);
        var b = (await f.Reader.ReadOwnedAsync(f.Buyer.OwnerUserId, f.Buyer.Id))!;
        var s = (await f.Reader.ReadOwnedAsync(f.Seller.OwnerUserId, f.Seller.Id))!;
        Assert.Equal(99420m, b.CashBalance);
        Assert.Equal(900m, b.ReservedCash);
        Assert.Equal(98520m, b.AvailableCash);
        Assert.Equal(100000m, b.Nav);
        Assert.Equal(4L, Assert.Single(b.Positions).Quantity);
        Assert.Equal(145m, Assert.Single(b.Positions).MarkPrice);
        Assert.Equal(100580m, s.CashBalance);
        var held = Assert.Single(s.Positions);
        Assert.Equal(6L, held.Quantity);
        Assert.Equal(6L, held.ReservedShares);
        Assert.Equal(0L, held.AvailableShares);
        Assert.Equal(2L, b.Version);
        Assert.Equal(2L, s.Version);
    }

    [Fact]
    public async Task Wrong_owner_and_missing_account_return_no_snapshot()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Null(await f.Reader.ReadOwnedAsync(f.Seller.OwnerUserId, f.Buyer.Id));
        Assert.Null(await f.Reader.ReadOwnedAsync(f.Buyer.OwnerUserId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Concurrent_release_and_read_never_mix_reservation_with_wrong_version()
    {
        await using var f = await Fixture.CreateAsync();
        var order = Guid.NewGuid();
        await f.Buy(order, 100, 150m);
        var read = f.Reader.ReadOwnedAsync(f.Buyer.OwnerUserId, f.Buyer.Id);
        var release = f.Release(order);
        await Task.WhenAll(read, release);
        var s = (await read)!;
        Assert.Equal(100000m, s.CashBalance);
        Assert.True((s.Version == 1 && s.ReservedCash == 15000m && s.AvailableCash == 85000m) ||
                    (s.Version == 2 && s.ReservedCash == 0m && s.AvailableCash == 100000m));
        Assert.Equal(2L, (await f.Reader.ReadOwnedAsync(f.Buyer.OwnerUserId, f.Buyer.Id))!.Version);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<QuantDeskDbContext> _options;
        public CompteTrading Buyer { get; }
        public CompteTrading Seller { get; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public string InstrumentId { get; } = "EQ-" + Guid.NewGuid().ToString("N");
        public PortfolioReservationService Service => new(_options);
        public PortfolioSnapshotReader Reader => new(_options);
        public ReservedEquitySettlementService Settlement => new(_options);
        private Fixture(DbContextOptions<QuantDeskDbContext> options)
        {
            _options = options;
            Buyer = CompteTrading.OpenPersonal(Guid.NewGuid(), 100000m);
            Seller = CompteTrading.OpenPersonal(Guid.NewGuid(), 100000m);
        }
        public QuantDeskDbContext OpenDb() => new(_options);
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
