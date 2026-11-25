using System;
using QuantDesk.Modules.Portfolio.Domain.Rules;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class EquityReservationRulesTests
{
    [Fact]
    public void Buy_reserves_limit_notional_without_spending_cash()
    {
        const decimal settled = 100_000m;
        var reserved = EquityReservationRules.RequiredBuyCash(100, 150m);
        Assert.Equal(15_000m, reserved);
        Assert.Equal(85_000m, EquityReservationRules.AvailableCash(settled, reserved));
    }

    [Fact]
    public void Buy_cannot_reuse_already_reserved_cash()
    {
        Assert.True(EquityReservationRules.CanReserveBuy(100_000m, 15_000m, 85, 1_000m));
        Assert.False(EquityReservationRules.CanReserveBuy(100_000m, 15_000m, 86, 1_000m));
    }

    [Fact]
    public void Sell_cannot_reuse_already_reserved_shares()
    {
        Assert.Equal(60L, EquityReservationRules.AvailableShares(100, 40));
        Assert.True(EquityReservationRules.CanReserveSell(100, 40, 60));
        Assert.False(EquityReservationRules.CanReserveSell(100, 40, 61));
    }

    [Fact]
    public void Partial_buy_fill_preserves_remaining_limit_reservation()
    {
        var fill = EquityReservationRules.CalculateBuyFill(100, 150m, 40, 145m);
        Assert.Equal(60L, fill.RemainingQuantity);
        Assert.Equal(5_800m, fill.CashDebit);
        Assert.Equal(6_000m, fill.ReservationReduction);
        Assert.Equal(9_000m, fill.RemainingReservedCash);
        Assert.Equal(200m, fill.PriceImprovementReleased);
        Assert.Equal(85_200m, EquityReservationRules.AvailableCash(
            100_000m - fill.CashDebit, fill.RemainingReservedCash));
    }

    [Fact]
    public void Full_buy_fill_at_better_price_releases_all_unused_reservation()
    {
        var fill = EquityReservationRules.CalculateBuyFill(100, 150m, 100, 145m);
        Assert.Equal(0L, fill.RemainingQuantity);
        Assert.Equal(14_500m, fill.CashDebit);
        Assert.Equal(15_000m, fill.ReservationReduction);
        Assert.Equal(0m, fill.RemainingReservedCash);
        Assert.Equal(500m, fill.PriceImprovementReleased);
        Assert.Equal(85_500m, EquityReservationRules.AvailableCash(
            100_000m - fill.CashDebit, fill.RemainingReservedCash));
    }

    [Fact]
    public void Sequential_partial_fills_conserve_cash_and_reservation()
    {
        var first = EquityReservationRules.CalculateBuyFill(100, 150m, 40, 145m);
        var second = EquityReservationRules.CalculateBuyFill(first.RemainingQuantity, 150m, 60, 149m);
        Assert.Equal(0L, second.RemainingQuantity);
        Assert.Equal(0m, second.RemainingReservedCash);
        Assert.Equal(15_000m, first.ReservationReduction + second.ReservationReduction);
        Assert.Equal(14_740m, first.CashDebit + second.CashDebit);
        Assert.Equal(260m, first.PriceImprovementReleased + second.PriceImprovementReleased);
    }

    [Fact]
    public void Partial_sell_fill_leaves_only_unfilled_quantity()
    {
        Assert.Equal(60L, EquityReservationRules.RemainingQuantityAfterFill(100, 40));
        Assert.Equal(0L, EquityReservationRules.RemainingQuantityAfterFill(60, 60));
    }

    [Fact]
    public void Buy_execution_above_limit_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EquityReservationRules.CalculateBuyFill(100, 150m, 1, 150.0001m));
    }

    [Fact]
    public void Overfill_and_nonpositive_fill_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EquityReservationRules.RemainingQuantityAfterFill(100, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EquityReservationRules.RemainingQuantityAfterFill(100, 0));
    }

    [Fact]
    public void Invalid_cash_and_stock_states_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => EquityReservationRules.AvailableCash(100m, 101m));
        Assert.Throws<InvalidOperationException>(() => EquityReservationRules.AvailableShares(100, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityReservationRules.AvailableCash(-1m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityReservationRules.AvailableShares(100, -1));
    }

    [Fact]
    public void Invalid_order_quantity_and_price_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityReservationRules.RequiredBuyCash(0, 150m));
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityReservationRules.RequiredBuyCash(1, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityReservationRules.RequiredBuyCash(1, 1.00001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityReservationRules.CanReserveSell(100, 0, -1));
    }

    [Fact]
    public void Fractional_cash_precision_is_preserved_and_overflow_is_rejected()
    {
        Assert.Equal(0.0003m, EquityReservationRules.RequiredBuyCash(3, 0.0001m));
        Assert.Throws<OverflowException>(() => EquityReservationRules.RequiredBuyCash(2, decimal.MaxValue));
    }
}
