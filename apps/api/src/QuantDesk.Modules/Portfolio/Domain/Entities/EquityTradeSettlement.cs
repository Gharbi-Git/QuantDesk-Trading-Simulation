namespace QuantDesk.Modules.Portfolio.Domain.Entities;

/// <summary>
/// A unique, durable accounting receipt for a trusted stock execution.
/// The M06 matching engine must authenticate and validate fills before invoking settlement.
/// No external HTTP endpoint exposes this entity or the settlement service.
/// </summary>
public sealed class EquityTradeSettlement
{
    public Guid ExecutionId { get; private set; }
    public Guid BuyerAccountId { get; private set; }
    public Guid SellerAccountId { get; private set; }
    public string InstrumentId { get; private set; } = string.Empty;
    public long Quantity { get; private set; }
    public decimal ExecutionPrice { get; private set; }
    public Guid? BuyOrderId { get; private set; }
    public Guid? SellOrderId { get; private set; }
    public Guid? SessionId { get; private set; }
    public DateTime SettledAtUtc { get; private set; }

    private EquityTradeSettlement() { }

    internal static EquityTradeSettlement FromTrustedExecution(TrustedEquityExecution execution, Guid? buyOrderId = null, Guid? sellOrderId = null, Guid? sessionId = null)
    {
        return new EquityTradeSettlement
        {
            ExecutionId = execution.ExecutionId,
            BuyerAccountId = execution.BuyerAccountId,
            SellerAccountId = execution.SellerAccountId,
            InstrumentId = execution.InstrumentId.Trim(),
            Quantity = execution.Quantity,
            ExecutionPrice = execution.ExecutionPrice,
            BuyOrderId = buyOrderId,
            SellOrderId = sellOrderId,
            SessionId = sessionId,
            SettledAtUtc = DateTime.UtcNow
        };
    }

    internal bool Matches(TrustedEquityExecution execution, Guid? buyOrderId = null, Guid? sellOrderId = null, Guid? sessionId = null) =>
        BuyOrderId == buyOrderId && SellOrderId == sellOrderId && SessionId == sessionId &&
        ExecutionId == execution.ExecutionId &&
        BuyerAccountId == execution.BuyerAccountId &&
        SellerAccountId == execution.SellerAccountId &&
        string.Equals(InstrumentId, execution.InstrumentId.Trim(), StringComparison.Ordinal) &&
        Quantity == execution.Quantity &&
        ExecutionPrice == execution.ExecutionPrice;
}

/// <summary>
/// Internal integration contract for a fill that the matching engine has already authorized.
/// In particular, do not construct this from untrusted browser values.
/// </summary>
public sealed record TrustedEquityExecution(
    Guid ExecutionId,
    Guid BuyerAccountId,
    Guid SellerAccountId,
    string InstrumentId,
    long Quantity,
    decimal ExecutionPrice);

public sealed record EquitySettlementResult(Guid ExecutionId, bool AlreadySettled);
