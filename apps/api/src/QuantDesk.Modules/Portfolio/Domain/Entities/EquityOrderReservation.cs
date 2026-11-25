using QuantDesk.Modules.Portfolio.Domain.Rules;

namespace QuantDesk.Modules.Portfolio.Domain.Entities;

public enum EquityReservationSide { Buy, Sell }
public enum EquityReservationStatus { Active, Filled, Released }

/// <summary>
/// Storage/lifecycle foundation for an already authorized equity limit order.
/// Creation alone does not check account availability or authorize an order.
/// A future application service must lock the account and validate aggregate
/// availability before inserting, releasing or consuming any reservation.
/// Fill idempotency belongs to the atomic settlement receipt, not this entity.
/// </summary>
public sealed class EquityOrderReservation
{
    private const decimal MaxStoredMoney = 9999999999999999.9999m;

    public Guid OrderId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid SessionId { get; private set; }
    public string InstrumentId { get; private set; } = string.Empty;
    public EquityReservationSide Side { get; private set; }
    public long OriginalQuantity { get; private set; }
    public long FilledQuantity { get; private set; }
    public decimal LimitPrice { get; private set; }
    public EquityReservationStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long Version { get; private set; }

    public long RemainingQuantity => Status == EquityReservationStatus.Active
        ? OriginalQuantity - FilledQuantity : 0L;
    public decimal ReservedCash => Side == EquityReservationSide.Buy && RemainingQuantity > 0
        ? EquityReservationRules.RequiredBuyCash(RemainingQuantity, LimitPrice) : 0m;
    public long ReservedShares => Side == EquityReservationSide.Sell ? RemainingQuantity : 0L;

    private EquityOrderReservation() { }

    public static EquityOrderReservation Create(Guid orderId, Guid accountId, Guid sessionId,
        string instrumentId, EquityReservationSide side, long quantity, decimal limitPrice)
    {
        if (orderId == Guid.Empty || accountId == Guid.Empty || sessionId == Guid.Empty)
            throw new ArgumentException("Order, account and session IDs are required.");
        if (string.IsNullOrWhiteSpace(instrumentId) || instrumentId.Trim().Length > 128)
            throw new ArgumentException("A canonical instrument ID is required.", nameof(instrumentId));
        if (side is not (EquityReservationSide.Buy or EquityReservationSide.Sell))
            throw new ArgumentOutOfRangeException(nameof(side));
        var notional = EquityReservationRules.RequiredBuyCash(quantity, limitPrice);
        if (limitPrice > MaxStoredMoney || notional > MaxStoredMoney)
            throw new ArgumentOutOfRangeException(nameof(limitPrice), "Order notional exceeds supported cash precision.");
        return new EquityOrderReservation
        {
            OrderId = orderId, AccountId = accountId, SessionId = sessionId,
            InstrumentId = instrumentId.Trim(), Side = side, OriginalQuantity = quantity,
            LimitPrice = limitPrice, Status = EquityReservationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Pure lifecycle mutation. Call only in the future atomic settlement transaction.</summary>
    public void ApplyFill(long quantity, decimal executionPrice)
    {
        if (Status != EquityReservationStatus.Active)
            throw new InvalidOperationException("Reservation is not active.");
        var remaining = EquityReservationRules.RemainingQuantityAfterFill(RemainingQuantity, quantity);
        var notional = EquityReservationRules.RequiredBuyCash(quantity, executionPrice);
        if (notional > MaxStoredMoney)
            throw new ArgumentOutOfRangeException(nameof(executionPrice), "Execution exceeds supported cash precision.");
        if (Side == EquityReservationSide.Buy)
            EquityReservationRules.CalculateBuyFill(RemainingQuantity, LimitPrice, quantity, executionPrice);
        else if (executionPrice < LimitPrice)
            throw new InvalidOperationException("Sell execution is below its limit price.");

        var version = checked(Version + 1);
        FilledQuantity = OriginalQuantity - remaining;
        Status = remaining == 0 ? EquityReservationStatus.Filled : EquityReservationStatus.Active;
        Version = version;
    }

    /// <summary>Release the unused portion after a trusted terminal order decision.</summary>
    public void Release()
    {
        if (Status == EquityReservationStatus.Released) return;
        if (Status != EquityReservationStatus.Active)
            throw new InvalidOperationException("A fully filled reservation cannot be released.");
        var version = checked(Version + 1);
        Status = EquityReservationStatus.Released;
        Version = version;
    }
}
