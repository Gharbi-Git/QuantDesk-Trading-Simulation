using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Domain.Entities;

namespace QuantDesk.Modules.Portfolio.Infrastructure.Services;

/// <summary>Internal trusted execution adapter; one fresh context per attempt.
/// The caller must authorize the session and reconcile sequenced engine events.
/// Never expose this service as a browser endpoint.</summary>
public sealed class ReservedEquitySettlementService
{
    private readonly DbContextOptions<QuantDeskDbContext> _options;
    public ReservedEquitySettlementService(DbContextOptions<QuantDeskDbContext> options) => _options = options;

    public async Task<EquitySettlementResult> SettleAsync(TrustedReservedEquityExecution execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.BuyOrderId == Guid.Empty || execution.SellOrderId == Guid.Empty ||
            execution.BuyOrderId == execution.SellOrderId || execution.SessionId == Guid.Empty)
            throw new ArgumentException("Distinct order IDs and a session ID are required.");
        await using var db = new QuantDeskDbContext(_options);
        return await new EquityTradeSettlementService(db).SettleCoreAsync(
            new TrustedEquityExecution(execution.ExecutionId, execution.BuyerAccountId,
                execution.SellerAccountId, execution.InstrumentId, execution.Quantity, execution.ExecutionPrice),
            execution, ct);
    }
}
