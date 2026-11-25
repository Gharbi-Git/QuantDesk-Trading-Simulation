using QuantDesk.Modules.Portfolio.Domain.Entities;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class EquityOrderReservationTests
{
    private static EquityOrderReservation Create(EquityReservationSide side = EquityReservationSide.Buy) =>
        EquityOrderReservation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "TEST-EQUITY", side, 100, 150m);

    [Fact]
    public void Buy_partial_fill_preserves_remaining_reservation()
    {
        var reservation = Create();
        Assert.Equal(15000m, reservation.ReservedCash);
        reservation.ApplyFill(40, 145m);
        Assert.Equal(40L, reservation.FilledQuantity);
        Assert.Equal(60L, reservation.RemainingQuantity);
        Assert.Equal(9000m, reservation.ReservedCash);
        Assert.Equal(EquityReservationStatus.Active, reservation.Status);
        reservation.ApplyFill(60, 150m);
        Assert.Equal(EquityReservationStatus.Filled, reservation.Status);
        Assert.Equal(0m, reservation.ReservedCash);
        Assert.Equal(100L, reservation.FilledQuantity);
        Assert.Equal(2L, reservation.Version);
    }

    [Fact]
    public void Sell_partial_fill_and_release_preserve_filled_quantity()
    {
        var reservation = Create(EquityReservationSide.Sell);
        Assert.Equal(100L, reservation.ReservedShares);
        Assert.Equal(0m, reservation.ReservedCash);
        reservation.ApplyFill(40, 155m);
        Assert.Equal(60L, reservation.ReservedShares);
        reservation.Release();
        Assert.Equal(0L, reservation.ReservedShares);
        Assert.Equal(40L, reservation.FilledQuantity);
        Assert.Equal(EquityReservationStatus.Released, reservation.Status);
        reservation.Release();
        Assert.Equal(2L, reservation.Version);
    }

    [Fact]
    public void Invalid_fills_do_not_mutate_reservation()
    {
        var buy = Create();
        Assert.Throws<InvalidOperationException>(() => buy.ApplyFill(1, 151m));
        Assert.Throws<ArgumentOutOfRangeException>(() => buy.ApplyFill(101, 149m));
        Assert.Equal(100L, buy.RemainingQuantity);
        Assert.Equal(0L, buy.Version);
        var sell = Create(EquityReservationSide.Sell);
        Assert.Throws<InvalidOperationException>(() => sell.ApplyFill(1, 149m));
        Assert.Equal(100L, sell.ReservedShares);
        Assert.Equal(0L, sell.Version);
    }

    [Fact]
    public void Terminal_reservations_cannot_be_filled_again()
    {
        var cancelled = Create();
        cancelled.Release();
        Assert.Throws<InvalidOperationException>(() => cancelled.ApplyFill(1, 149m));
        var filled = Create();
        filled.ApplyFill(100, 149m);
        Assert.Throws<InvalidOperationException>(() => filled.ApplyFill(1, 149m));
        Assert.Throws<InvalidOperationException>(() => filled.Release());
    }

    [Fact]
    public void Invalid_ids_and_storage_precision_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => EquityOrderReservation.Create(Guid.Empty,
            Guid.NewGuid(), Guid.NewGuid(), "TEST-EQUITY", EquityReservationSide.Buy, 1, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => EquityOrderReservation.Create(Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "TEST-EQUITY", EquityReservationSide.Buy, 2, 9999999999999999m));
    }
}
