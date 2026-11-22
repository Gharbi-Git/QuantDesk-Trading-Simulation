using System.Data;
using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;

namespace QuantDesk.Modules.Portfolio.Infrastructure.Services;

/// <summary>
/// Stock-only accounting integration for a VERIFIED matching-engine fill.
/// Uses one database transaction, deterministic row locks, an execution-ID
/// uniqueness constraint, and explicit EF optimistic concurrency tokens.
/// This does not place orders or match trades; it is intentionally not an HTTP API.
/// Fee policies, reservations, and option settlement remain separate work.
/// </summary>
public sealed class EquityTradeSettlementService
{
    private readonly QuantDeskDbContext _db;

    public EquityTradeSettlementService(QuantDeskDbContext db)
    {
        _db = db;
    }

    public async Task<EquitySettlementResult> SettleTrustedFillAsync(
        TrustedEquityExecution execution,
        CancellationToken cancellationToken = default)
    {
        Validate(execution);

        // READ COMMITTED: after waiting for another transaction's row lock,
        // a new statement sees its committed settlement receipt.
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        var prior = await _db.EquitySettlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ExecutionId == execution.ExecutionId, cancellationToken);
        if (prior is not null)
        {
            VerifyReplay(prior, execution);
            return new EquitySettlementResult(execution.ExecutionId, true);
        }

        // Every settlement writer MUST acquire account row locks in the same order.
        // This serializes cash and inventory checks even across different buyers.
        var accounts = new Dictionary<Guid, CompteTrading>();
        foreach (var id in new[] { execution.BuyerAccountId, execution.SellerAccountId }.OrderBy(id => id))
        {
            // ToListAsync executes this SELECT ... FOR UPDATE directly, without
            // EF composing a LIMIT/subquery around the row-locking statement.
            var locked = await _db.ComptesTrading
                .FromSqlInterpolated($"SELECT * FROM comptes_trading WHERE \"Id\" = {id} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var account = locked.SingleOrDefault() ??
                throw new InvalidOperationException("A settlement trading account was not found.");
            accounts.Add(id, account);
        }

        // A concurrent attempt with the same execution ID may have committed
        // while this transaction waited for the first account lock.
        prior = await _db.EquitySettlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ExecutionId == execution.ExecutionId, cancellationToken);
        if (prior is not null)
        {
            VerifyReplay(prior, execution);
            return new EquitySettlementResult(execution.ExecutionId, true);
        }

        var buyer = accounts[execution.BuyerAccountId];
        var seller = accounts[execution.SellerAccountId];
        if (buyer.Currency != seller.Currency || buyer.Currency != "USD")
            throw new InvalidOperationException("Only same-currency USD stock settlement is supported.");

        var gross = checked(execution.Quantity * execution.ExecutionPrice);
        if (gross <= 0m || decimal.Round(gross, 4) != gross)
            throw new InvalidOperationException("Execution notional is outside supported cash precision.");
        if (buyer.CashBalance < gross)
            throw new InvalidOperationException("Insufficient buyer cash for this execution.");

        var instrumentId = execution.InstrumentId.Trim();
        var sellerPosition = await _db.PositionsAction.SingleOrDefaultAsync(
            p => p.CompteTradingId == seller.Id && p.InstrumentId == instrumentId,
            cancellationToken);
        if (sellerPosition is null || sellerPosition.Quantity < execution.Quantity)
            throw new InvalidOperationException("Insufficient owned seller shares for this execution.");

        var buyerPosition = await _db.PositionsAction.SingleOrDefaultAsync(
            p => p.CompteTradingId == buyer.Id && p.InstrumentId == instrumentId,
            cancellationToken);

        // All mutations are staged in EF, then saved atomically below.
        sellerPosition.Sell(execution.Quantity, execution.ExecutionPrice);
        if (buyerPosition is null)
            _db.PositionsAction.Add(PositionAction.OpenWithBuy(
                buyer.Id, instrumentId, execution.Quantity, execution.ExecutionPrice));
        else
            buyerPosition.Buy(execution.Quantity, execution.ExecutionPrice);

        buyer.ApplySettlementCash(-gross);
        seller.ApplySettlementCash(gross);

        _db.CashJournal.Add(new CashJournalEntry(
            buyer.Id, -gross, $"stock-fill:{execution.ExecutionId:D}:buyer"));
        _db.CashJournal.Add(new CashJournalEntry(
            seller.Id, gross, $"stock-fill:{execution.ExecutionId:D}:seller"));
        _db.EquitySettlements.Add(EquityTradeSettlement.FromTrustedExecution(execution));

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EquitySettlementResult(execution.ExecutionId, false);
    }

    private static void VerifyReplay(
        EquityTradeSettlement prior, TrustedEquityExecution execution)
    {
        if (!prior.Matches(execution))
            throw new InvalidOperationException(
                "An execution ID was reused with different settlement details.");
    }

    private static void Validate(TrustedEquityExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.ExecutionId == Guid.Empty || execution.BuyerAccountId == Guid.Empty ||
            execution.SellerAccountId == Guid.Empty ||
            execution.BuyerAccountId == execution.SellerAccountId)
            throw new ArgumentException("Distinct buyer, seller, and execution IDs are required.");
        if (string.IsNullOrWhiteSpace(execution.InstrumentId) ||
            execution.InstrumentId.Trim().Length > 128)
            throw new ArgumentException("A valid canonical instrument ID is required.");
        if (execution.Quantity <= 0 || execution.ExecutionPrice <= 0m ||
            decimal.Round(execution.ExecutionPrice, 4) != execution.ExecutionPrice)
            throw new ArgumentOutOfRangeException(nameof(execution),
                "Positive quantity and price with up to four decimals are required.");
    }
}

