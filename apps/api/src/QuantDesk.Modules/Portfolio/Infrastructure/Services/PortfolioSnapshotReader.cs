using System.Data;
using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Application;
using QuantDesk.Modules.Portfolio.Application.Abstractions;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Domain.Rules;

namespace QuantDesk.Modules.Portfolio.Infrastructure.Services;

/// <summary>Read-only, coherent account view. Every query sees the same PostgreSQL MVCC
/// snapshot, including when a reservation or settlement commits concurrently.
/// Indicative own-trade marks are not session quotes or risk-approved live marks.</summary>
public sealed class PortfolioSnapshotReader : IPortfolioSnapshotReader
{
    private readonly DbContextOptions<QuantDeskDbContext> _options;
    private static readonly TimeSpan MarkAgeLimit = TimeSpan.FromHours(24);
    public const string MarkPolicy = "LAST_OWN_SETTLED_QUANTDESK_TRADE_24H";

    public PortfolioSnapshotReader(DbContextOptions<QuantDeskDbContext> options) => _options = options;

    public async Task<PortfolioAccountSnapshot?> ReadOwnedAsync(Guid ownerUserId, Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty || accountId == Guid.Empty)
            throw new ArgumentException("Owner and account IDs are required.");
        await using var db = new QuantDeskDbContext(_options);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var account = await db.ComptesTrading.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (account is null) return null;
        var asOf = DateTime.UtcNow;
        var positions = await db.PositionsAction.AsNoTracking()
            .Where(p => p.CompteTradingId == accountId).OrderBy(p => p.InstrumentId)
            .ToListAsync(cancellationToken);
        var reservations = await db.EquityOrderReservations.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.Status == EquityReservationStatus.Active)
            .ToListAsync(cancellationToken);
        var reservedCash = reservations.Sum(r => r.ReservedCash);
        var availableCash = EquityReservationRules.AvailableCash(account.CashBalance, reservedCash);
        var reservedShares = reservations.Where(r => r.Side == EquityReservationSide.Sell)
            .GroupBy(r => r.InstrumentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.ReservedShares), StringComparer.Ordinal);
        var positionIds = positions.Select(p => p.InstrumentId).ToHashSet(StringComparer.Ordinal);
        if (reservedShares.Keys.Any(id => !positionIds.Contains(id)))
            throw new InvalidOperationException("A stock reservation has no owned position.");
        var symbols = positions.Where(p => p.Quantity > 0).Select(p => p.InstrumentId).ToArray();
        var cutoff = asOf - MarkAgeLimit;
        var marks = symbols.Length == 0 ? new List<PortfolioValuationCalculator.Mark>() :
            await db.EquitySettlements.AsNoTracking()
                .Where(e => symbols.Contains(e.InstrumentId) &&
                    (e.BuyerAccountId == accountId || e.SellerAccountId == accountId) &&
                    e.SettledAtUtc >= cutoff && e.SettledAtUtc <= asOf)
                .OrderByDescending(e => e.SettledAtUtc).ThenByDescending(e => e.ExecutionId)
                .Select(e => new PortfolioValuationCalculator.Mark(e.InstrumentId, e.ExecutionPrice, e.SettledAtUtc))
                .ToListAsync(cancellationToken);
        var valuation = PortfolioValuationCalculator.Calculate(account.CashBalance,
            positions.Select(p => new PortfolioValuationCalculator.Holding(p.InstrumentId,
                p.Quantity, p.CostBasis, p.RealizedPnl)), marks, asOf, MarkAgeLimit);
        var rows = valuation.Positions.Select(p =>
        {
            var reserved = reservedShares.GetValueOrDefault(p.InstrumentId);
            return new PortfolioPositionSnapshot(p.InstrumentId, p.Quantity, reserved,
                EquityReservationRules.AvailableShares(p.Quantity, reserved), p.CostBasis,
                p.AverageAcquisitionCost, p.RealizedPnl, p.MarkPrice, p.MarkAsOfUtc,
                p.MarketValue, p.UnrealizedPnl);
        }).ToList().AsReadOnly();
        var result = new PortfolioAccountSnapshot(account.Id, account.Currency, account.Version,
            asOf, account.CashBalance, reservedCash, availableCash, valuation.TotalCostBasis,
            valuation.RealizedPnl, valuation.MarketValue, valuation.Nav, valuation.UnrealizedPnl,
            MarkPolicy, valuation.MissingMarkInstruments, rows);
        await tx.CommitAsync(cancellationToken);
        return result;
    }
}
