using System;

namespace QuantDesk.Modules.Portfolio.Domain.Rules;

/// <summary>
/// Pure, pre-fee equity limit-order reservation arithmetic. These rules do not
/// authorize an order, persist a reservation, or mutate settled balances.
/// Amounts use decimal with at most four fractional places, matching the
/// current stock settlement service. Instrument-specific tick validation is external.
/// </summary>
public static class EquityReservationRules
{
    public static decimal RequiredBuyCash(long quantity, decimal limitPrice)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        ValidatePositivePrice(limitPrice, nameof(limitPrice));
        return checked(quantity * limitPrice);
    }

    public static decimal AvailableCash(decimal settledCash, decimal reservedCash)
    {
        ValidateCash(settledCash, nameof(settledCash));
        ValidateCash(reservedCash, nameof(reservedCash));
        if (reservedCash > settledCash)
            throw new InvalidOperationException("Reserved cash exceeds settled cash.");
        return settledCash - reservedCash;
    }

    public static long AvailableShares(long ownedShares, long reservedShares)
    {
        if (ownedShares < 0)
            throw new ArgumentOutOfRangeException(nameof(ownedShares));
        if (reservedShares < 0)
            throw new ArgumentOutOfRangeException(nameof(reservedShares));
        if (reservedShares > ownedShares)
            throw new InvalidOperationException("Reserved shares exceed owned shares.");
        return ownedShares - reservedShares;
    }

    public static bool CanReserveBuy(decimal settledCash, decimal alreadyReservedCash,
        long quantity, decimal limitPrice) =>
        RequiredBuyCash(quantity, limitPrice) <= AvailableCash(settledCash, alreadyReservedCash);

    public static bool CanReserveSell(long ownedShares, long alreadyReservedShares, long quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        return quantity <= AvailableShares(ownedShares, alreadyReservedShares);
    }

    public static long RemainingQuantityAfterFill(long outstandingQuantity, long fillQuantity)
    {
        if (outstandingQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(outstandingQuantity));
        if (fillQuantity <= 0 || fillQuantity > outstandingQuantity)
            throw new ArgumentOutOfRangeException(nameof(fillQuantity));
        return outstandingQuantity - fillQuantity;
    }

    /// <summary>
    /// Calculates a BUY fill against the outstanding quantity at the original
    /// limit. The actual cash debit and reduction of reserved cash are distinct.
    /// Callers must apply both atomically with the validated settlement.
    /// </summary>
    public static BuyReservationFill CalculateBuyFill(long outstandingQuantity,
        decimal limitPrice, long fillQuantity, decimal executionPrice)
    {
        var remaining = RemainingQuantityAfterFill(outstandingQuantity, fillQuantity);
        var before = RequiredBuyCash(outstandingQuantity, limitPrice);
        ValidatePositivePrice(executionPrice, nameof(executionPrice));
        if (executionPrice > limitPrice)
            throw new InvalidOperationException("Buy execution exceeds its limit price.");
        var debit = checked(fillQuantity * executionPrice);
        var after = remaining == 0 ? 0m : RequiredBuyCash(remaining, limitPrice);
        var reduction = before - after;
        return new BuyReservationFill(remaining, debit, reduction, after, reduction - debit);
    }

    private static void ValidatePositivePrice(decimal price, string parameter)
    {
        if (price <= 0m || decimal.Round(price, 4) != price)
            throw new ArgumentOutOfRangeException(parameter, "Price must be positive with up to four decimals.");
    }

    private static void ValidateCash(decimal amount, string parameter)
    {
        if (amount < 0m || decimal.Round(amount, 4) != amount)
            throw new ArgumentOutOfRangeException(parameter, "Cash must be nonnegative with up to four decimals.");
    }
}

public readonly record struct BuyReservationFill(
    long RemainingQuantity,
    decimal CashDebit,
    decimal ReservationReduction,
    decimal RemainingReservedCash,
    decimal PriceImprovementReleased);
