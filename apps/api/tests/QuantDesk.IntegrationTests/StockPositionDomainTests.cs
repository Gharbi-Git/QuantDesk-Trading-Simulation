using System;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class StockPositionDomainTests
{
    private static PositionAction Open(long quantity = 10, decimal price = 100m) =>
        PositionAction.OpenWithBuy(Guid.NewGuid(), "AAPL", quantity, price);

    [Fact]
    public void Opening_buy_stores_account_instrument_quantity_and_cost_basis()
    {
        var ownerAccount = Guid.NewGuid();
        var p = PositionAction.OpenWithBuy(ownerAccount, "AAPL", 10, 100m);

        Assert.Equal(ownerAccount, p.CompteTradingId);
        Assert.Equal("AAPL", p.InstrumentId);
        Assert.Equal(10, p.Quantity);
        Assert.Equal(1000m, p.CostBasis);
        Assert.Equal(100m, p.AverageAcquisitionCost);
        Assert.Equal(0m, p.RealizedPnl);
    }

    [Fact]
    public void Two_buys_update_weighted_average_acquisition_cost()
    {
        var p = Open(10, 100m);
        p.Buy(10, 120m);

        Assert.Equal(20, p.Quantity);
        Assert.Equal(2200m, p.CostBasis);
        Assert.Equal(110m, p.AverageAcquisitionCost);
        Assert.Equal(0m, p.RealizedPnl);
    }

    [Fact]
    public void Partial_sell_realizes_profit_without_changing_average_cost()
    {
        var p = Open(10, 100m);
        p.Buy(10, 120m);

        var fillPnl = p.Sell(5, 130m);

        Assert.Equal(100m, fillPnl);
        Assert.Equal(15, p.Quantity);
        Assert.Equal(1650m, p.CostBasis);
        Assert.Equal(110m, p.AverageAcquisitionCost);
        Assert.Equal(100m, p.RealizedPnl);
    }

    [Fact]
    public void Sell_below_average_realizes_loss()
    {
        var p = Open(10, 100m);

        Assert.Equal(-100m, p.Sell(5, 80m));
        Assert.Equal(-100m, p.RealizedPnl);
        Assert.Equal(5, p.Quantity);
        Assert.Equal(500m, p.CostBasis);
    }

    [Fact]
    public void Closing_position_clears_quantity_basis_and_average_but_keeps_realized_pnl()
    {
        var p = Open(10, 100m);
        p.Buy(10, 120m);
        Assert.Equal(100m, p.Sell(5, 130m));

        Assert.Equal(225m, p.Sell(15, 125m));
        Assert.Equal(0, p.Quantity);
        Assert.Equal(0m, p.CostBasis);
        Assert.Equal(0m, p.AverageAcquisitionCost);
        Assert.Equal(325m, p.RealizedPnl);
    }

    [Fact]
    public void Reopening_closed_position_starts_new_cost_basis_not_old_average()
    {
        var p = Open(2, 100m);
        p.Sell(2, 110m);
        p.Buy(3, 70m);

        Assert.Equal(3, p.Quantity);
        Assert.Equal(70m, p.AverageAcquisitionCost);
        Assert.Equal(210m, p.CostBasis);
        Assert.Equal(20m, p.RealizedPnl);
    }

    [Fact]
    public void Overselling_rejects_without_mutating_position()
    {
        var p = Open();

        Assert.Throws<InvalidOperationException>(() => p.Sell(11, 110m));
        Assert.Equal(10, p.Quantity);
        Assert.Equal(1000m, p.CostBasis);
        Assert.Equal(0m, p.RealizedPnl);
    }

    [Fact]
    public void Zero_and_negative_quantities_are_rejected()
    {
        var p = Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Buy(0, 100m));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Buy(-1, 100m));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Sell(0, 100m));
        Assert.Equal(10, p.Quantity);
    }

    [Fact]
    public void Zero_and_negative_execution_prices_are_rejected()
    {
        var p = Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Buy(1, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Sell(1, -1m));
        Assert.Equal(10, p.Quantity);
    }

    [Fact]
    public void Missing_account_and_missing_instrument_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PositionAction.OpenWithBuy(Guid.Empty, "AAPL", 1, 100m));
        Assert.Throws<ArgumentException>(() =>
            PositionAction.OpenWithBuy(Guid.NewGuid(), "  ", 1, 100m));
    }

    [Fact]
    public void Fractional_average_cost_allocates_basis_and_full_close_removes_residue()
    {
        var p = Open(1, 10m);
        p.Buy(2, 10.01m); // $30.02 total across 3 shares => repeating decimal average

        Assert.Equal(0.99333333m, p.Sell(1, 11m));
        Assert.Equal(20.01333333m, p.CostBasis);
        Assert.Equal(1.98666667m, p.Sell(2, 11m));
        Assert.Equal(0m, p.CostBasis);
        Assert.Equal(0, p.Quantity);
        Assert.Equal(2.98m, p.RealizedPnl);
    }

    [Fact]
    public void Large_quantity_overflow_does_not_partially_mutate_position()
    {
        var p = Open(1, 1m);
        Assert.Throws<OverflowException>(() => p.Buy(long.MaxValue, 1m));
        Assert.Equal(1, p.Quantity);
        Assert.Equal(1m, p.CostBasis);
    }
}
