using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Domain.Rules;

namespace QuantDesk.Modules.Portfolio.Infrastructure.Services;

/// <summary>
/// Provisional internal M07 use case. Caller must authenticate and authorize the
/// order/session/instrument before calling. Release is only for a confirmed
/// terminal engine decision, after all preceding fills have been reconciled.
/// No HTTP exposure or matching-engine registration yet. Each operation owns
/// a fresh context and locks the same account row as equity settlement.
/// </summary>
public sealed class PortfolioReservationService
{
    private readonly DbContextOptions<QuantDeskDbContext> _options;
    public PortfolioReservationService(DbContextOptions<QuantDeskDbContext> options) => _options = options;

    public async Task<ReservationOutcome> ReserveAsync(Guid ownerUserId, Guid orderId,
        Guid accountId, Guid sessionId, string instrumentId, EquityReservationSide side,
        long quantity, decimal limitPrice, CancellationToken ct = default)
    {
        var candidate = EquityOrderReservation.Create(orderId, accountId, sessionId,
            instrumentId, side, quantity, limitPrice);
        await using var db = new QuantDeskDbContext(_options);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var account = await LockOwnedAccountAsync(db, ownerUserId, accountId, ct);
        var prior = await db.EquityOrderReservations.AsNoTracking()
            .SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (prior is not null)
        {
            if (!SameRequest(prior, candidate))
                throw new InvalidOperationException("Order ID already used with different reservation details.");
            await tx.CommitAsync(ct);
            return Snapshot(prior, true);
        }
        if (side == EquityReservationSide.Buy)
        {
            var reserved = await ReservationAvailability.ReservedCashAsync(db, accountId, ct);
            if (!EquityReservationRules.CanReserveBuy(account.CashBalance, reserved, quantity, limitPrice))
                throw new InvalidOperationException("Insufficient available cash for reservation.");
        }
        else
        {
            var held = await db.PositionsAction.AsNoTracking()
                .Where(p => p.CompteTradingId == accountId && p.InstrumentId == candidate.InstrumentId)
                .Select(p => (long?)p.Quantity).SingleOrDefaultAsync(ct) ?? 0L;
            var reserved = await ReservationAvailability.ReservedSharesAsync(db, accountId, candidate.InstrumentId, ct);
            if (!EquityReservationRules.CanReserveSell(held, reserved, quantity))
                throw new InvalidOperationException("Insufficient available shares for reservation.");
        }
        db.EquityOrderReservations.Add(candidate);
        account.AdvanceReservationVersion();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg &&
            pg.SqlState == PostgresErrorCodes.UniqueViolation &&
            pg.ConstraintName == "PK_equity_order_reservations")
        {
            // Concurrent reuse of an order ID across different accounts. The
            // transaction is disposed without commit; no assets are reserved.
            throw new InvalidOperationException("Order ID already used with different reservation details.", ex);
        }
        await tx.CommitAsync(ct);
        return Snapshot(candidate, false);
    }

    public async Task<ReservationOutcome> ReleaseConfirmedAsync(Guid ownerUserId, Guid accountId,
        Guid sessionId, Guid orderId, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty || orderId == Guid.Empty)
            throw new ArgumentException("Order and session IDs are required.");
        await using var db = new QuantDeskDbContext(_options);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var account = await LockOwnedAccountAsync(db, ownerUserId, accountId, ct);
        var reservation = await db.EquityOrderReservations.SingleOrDefaultAsync(r =>
            r.OrderId == orderId && r.AccountId == accountId && r.SessionId == sessionId, ct)
            ?? throw new InvalidOperationException("Reservation not found for account and session.");
        var replay = reservation.Status == EquityReservationStatus.Released;
        if (!replay)
        {
            reservation.Release();
            account.AdvanceReservationVersion();
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Snapshot(reservation, replay);
    }

    private static async Task<CompteTrading> LockOwnedAccountAsync(QuantDeskDbContext db,
        Guid ownerUserId, Guid accountId, CancellationToken ct)
    {
        if (ownerUserId == Guid.Empty || accountId == Guid.Empty)
            throw new ArgumentException("Owner and account IDs are required.");
        var rows = await db.ComptesTrading.FromSqlInterpolated(
            $"SELECT * FROM comptes_trading WHERE \"Id\" = {accountId} AND owner_user_id = {ownerUserId} FOR UPDATE")
            .ToListAsync(ct);
        var account = rows.SingleOrDefault() ?? throw new InvalidOperationException("Trading account not found for owner.");
        if (account.Currency != "USD") throw new InvalidOperationException("Only USD equity reservations are supported.");
        return account;
    }

    private static bool SameRequest(EquityOrderReservation a, EquityOrderReservation b) =>
        a.AccountId == b.AccountId && a.SessionId == b.SessionId &&
        string.Equals(a.InstrumentId, b.InstrumentId, StringComparison.Ordinal) &&
        a.Side == b.Side && a.OriginalQuantity == b.OriginalQuantity && a.LimitPrice == b.LimitPrice;

    private static ReservationOutcome Snapshot(EquityOrderReservation r, bool replay) =>
        new(r.OrderId, r.Status, r.RemainingQuantity, r.ReservedCash, r.ReservedShares, r.Version, replay);
}

// Internal implementation DTO, not a frozen shared wire contract.
public sealed record ReservationOutcome(Guid OrderId, EquityReservationStatus Status,
    long RemainingQuantity, decimal ReservedCash, long ReservedShares, long Version, bool AlreadyApplied);

/// <summary>Must only be used for decisions while holding the relevant account row lock.</summary>
internal static class ReservationAvailability
{
    internal static async Task<decimal> ReservedCashAsync(QuantDeskDbContext db, Guid accountId, CancellationToken ct)
    {
        var active = await db.EquityOrderReservations.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.Status == EquityReservationStatus.Active && r.Side == EquityReservationSide.Buy)
            .ToListAsync(ct);
        return active.Sum(r => r.ReservedCash);
    }

    internal static async Task<long> ReservedSharesAsync(QuantDeskDbContext db, Guid accountId,
        string instrumentId, CancellationToken ct)
    {
        var active = await db.EquityOrderReservations.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.Status == EquityReservationStatus.Active &&
                r.Side == EquityReservationSide.Sell && r.InstrumentId == instrumentId)
            .ToListAsync(ct);
        return active.Sum(r => r.ReservedShares);
    }
}
