using QuantDesk.Modules.Portfolio.Application;
using Xunit;
using H = QuantDesk.Modules.Portfolio.Application.PortfolioValuationCalculator.Holding;
using M = QuantDesk.Modules.Portfolio.Application.PortfolioValuationCalculator.Mark;

namespace QuantDesk.IntegrationTests;

public sealed class PortfolioValuationDomainTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    [Fact]
    public void Cash_only_portfolio_has_exact_nav_without_a_mark()
    {
        var result = PortfolioValuationCalculator.Calculate(100_000m,
            Array.Empty<H>(), Array.Empty<M>(), Now, MaxAge);
        Assert.Equal(100_000m, result.Nav);
        Assert.Equal(0m, result.UnrealizedPnl);
        Assert.Empty(result.MissingMarkInstruments);
    }

    [Fact]
    public void Valid_mark_calculates_nav_and_unrealized_pnl()
    {
        var result = PortfolioValuationCalculator.Calculate(10_000m,
            new[] { new H("AAPL", 10, 1_000m, 75m) },
            new[] { new M("AAPL", 120m, Now.AddMinutes(-5)) }, Now, MaxAge);
        Assert.Equal(1_200m, result.MarketValue);
        Assert.Equal(11_200m, result.Nav);
        Assert.Equal(200m, result.UnrealizedPnl);
        Assert.Equal(75m, result.RealizedPnl);
        Assert.Equal(100m, result.Positions.Single().AverageAcquisitionCost);
    }

    [Fact]
    public void Missing_one_mark_makes_total_nav_unavailable_not_partially_valued()
    {
        var result = PortfolioValuationCalculator.Calculate(1_000m,
            new[] { new H("AAPL", 2, 100m, 0m), new H("MSFT", 1, 200m, 0m) },
            new[] { new M("AAPL", 60m, Now) }, Now, MaxAge);
        Assert.Null(result.Nav);
        Assert.Null(result.MarketValue);
        Assert.Null(result.UnrealizedPnl);
        Assert.Equal(new[] { "MSFT" }, result.MissingMarkInstruments);
    }

    [Fact]
    public void Stale_or_future_marks_are_not_trusted()
    {
        var result = PortfolioValuationCalculator.Calculate(2_000m,
            new[] { new H("MSFT", 2, 200m, 0m) },
            new[] { new M("MSFT", 500m, Now.AddHours(-25)),
                    new M("MSFT", 600m, Now.AddMinutes(2)) }, Now, MaxAge);
        Assert.Null(result.Nav);
        Assert.Single(result.MissingMarkInstruments);
    }

    [Fact]
    public void Latest_eligible_mark_wins()
    {
        var result = PortfolioValuationCalculator.Calculate(0m,
            new[] { new H("AAPL", 1, 10m, 0m) },
            new[] { new M("AAPL", 11m, Now.AddHours(-4)),
                    new M("AAPL", 12m, Now.AddMinutes(-1)) }, Now, MaxAge);
        Assert.Equal(12m, result.Nav);
        Assert.Equal(2m, result.UnrealizedPnl);
    }

    [Fact]
    public void Closed_positions_retain_realized_pnl_without_a_mark()
    {
        var result = PortfolioValuationCalculator.Calculate(10m,
            new[] { new H("AAPL", 0, 0m, -25m) },
            Array.Empty<M>(), Now, MaxAge);
        Assert.Equal(10m, result.Nav);
        Assert.Equal(-25m, result.RealizedPnl);
    }

    [Fact]
    public void Duplicate_instruments_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => PortfolioValuationCalculator.Calculate(10m,
            new[] { new H("AAPL", 1, 10m, 0m), new H("AAPL", 2, 20m, 0m) },
            Array.Empty<M>(), Now, MaxAge));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    public void Inconsistent_holdings_are_rejected(long quantity, int basis)
    {
        Assert.Throws<ArgumentException>(() => PortfolioValuationCalculator.Calculate(100m,
            new[] { new H("AAPL", quantity, basis, 0m) },
            Array.Empty<M>(), Now, MaxAge));
    }
}
