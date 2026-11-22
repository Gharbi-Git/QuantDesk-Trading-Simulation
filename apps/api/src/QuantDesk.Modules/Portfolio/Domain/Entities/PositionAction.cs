using System;

namespace QuantDesk.Modules.Portfolio.Domain.Entities;

/// <summary>
/// Long-only stock holding belonging to one trading account's 1:1 portfolio.
/// This class calculates position cost basis and pre-fee realized P&L only.
/// Cash movements, execution idempotency and reservation/settlement are separate concerns.
/// </summary>
public sealed class PositionAction
{
    // Stored values; EF Core can rehydrate them through the private constructor/setters.
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid CompteTradingId { get; private set; }
    public Portefeuille Portefeuille { get; private set; } = null!;
    public string InstrumentId { get; private set; } = string.Empty;
    public long Quantity { get; private set; }
    public decimal CostBasis { get; private set; }
    public decimal RealizedPnl { get; private set; }

    // Derived, not separately stored: cannot become inconsistent with Quantity/CostBasis.
    public decimal AverageAcquisitionCost =>
        Quantity == 0 ? 0m : CostBasis / Quantity;

    private PositionAction() { }

    public static PositionAction OpenWithBuy(
        Guid compteTradingId,
        string instrumentId,
        long quantity,
        decimal executionPrice)
    {
        if (compteTradingId == Guid.Empty)
            throw new ArgumentException("A trading account is required.", nameof(compteTradingId));

        if (string.IsNullOrWhiteSpace(instrumentId))
            throw new ArgumentException("A canonical instrument ID is required.", nameof(instrumentId));

        if (instrumentId.Trim().Length > 128)
            throw new ArgumentOutOfRangeException(nameof(instrumentId), "Instrument ID is too long.");

        var position = new PositionAction
        {
            CompteTradingId = compteTradingId,
            InstrumentId = instrumentId.Trim()
        };

        position.Buy(quantity, executionPrice);
        return position;
    }

    /// <summary>Apply an already authorized and executed BUY fill.</summary>
    public void Buy(long quantity, decimal executionPrice)
    {
        ValidateFill(quantity, executionPrice);

        // Compute first, then mutate: overflow must not partially modify the position.
        var nextQuantity = checked(Quantity + quantity);
        var nextCostBasis = checked(CostBasis + quantity * executionPrice);

        Quantity = nextQuantity;
        CostBasis = nextCostBasis;
    }

    /// <summary>
    /// Apply an already authorized and executed SELL fill.
    /// Returns this fill's pre-fee realized P&L. The total is also accumulated.
    /// Uses eight decimal places for proportional average-cost allocation;
    /// the final sale releases the exact remaining basis to avoid residuals.
    /// </summary>
    public decimal Sell(long quantity, decimal executionPrice)
    {
        ValidateFill(quantity, executionPrice);

        if (quantity > Quantity)
            throw new InvalidOperationException("Cannot sell more shares than the position owns.");

        decimal releasedBasis = quantity == Quantity
            ? CostBasis
            : decimal.Round(
                checked(CostBasis * quantity) / Quantity,
                8,
                MidpointRounding.ToEven);

        var proceeds = checked(quantity * executionPrice);
        var realizedOnFill = checked(proceeds - releasedBasis);
        var nextRealized = checked(RealizedPnl + realizedOnFill);
        var remainingQuantity = Quantity - quantity;
        var remainingBasis = remainingQuantity == 0 ? 0m : CostBasis - releasedBasis;

        Quantity = remainingQuantity;
        CostBasis = remainingBasis;
        RealizedPnl = nextRealized;
        return realizedOnFill;
    }

    private static void ValidateFill(long quantity, decimal price)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Share quantity must be positive.");

        if (price <= 0m)
            throw new ArgumentOutOfRangeException(nameof(price), "Executed share price must be positive.");

        // Until instrument-specific tick sizes are defined, don't silently round fills.
        // A later shared Money/Price contract will enforce each instrument's tick rules.
    }
}
