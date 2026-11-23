namespace QuantDesk.Modules.Portfolio.Application;

/// <summary>
/// Read-model valuation: all marks are sourced by the calling server adapter.
/// Never substitute acquisition cost for a missing mark or silently produce partial NAV.
/// </summary>
public static class PortfolioValuationCalculator
{
    public sealed record Holding(string InstrumentId, long Quantity, decimal CostBasis, decimal RealizedPnl);
    public sealed record Mark(string InstrumentId, decimal Price, DateTime AsOfUtc);
    public sealed record MarkedHolding(
        string InstrumentId, long Quantity, decimal CostBasis, decimal AverageAcquisitionCost,
        decimal RealizedPnl, decimal? MarkPrice, DateTime? MarkAsOfUtc,
        decimal? MarketValue, decimal? UnrealizedPnl);
    public sealed record Snapshot(
        decimal CashBalance, decimal TotalCostBasis, decimal RealizedPnl,
        decimal? MarketValue, decimal? Nav, decimal? UnrealizedPnl,
        IReadOnlyList<string> MissingMarkInstruments,
        IReadOnlyList<MarkedHolding> Positions);

    public static Snapshot Calculate(
        decimal cashBalance,
        IEnumerable<Holding> holdings,
        IEnumerable<Mark> marks,
        DateTime asOfUtc,
        TimeSpan maxMarkAge)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        ArgumentNullException.ThrowIfNull(marks);
        if (cashBalance < 0) throw new ArgumentOutOfRangeException(nameof(cashBalance));
        if (maxMarkAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxMarkAge));
        if (asOfUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Valuation time must be UTC.", nameof(asOfUtc));

        // For each instrument, select the latest eligible verified reference mark.
        var validMarks = marks
            .Where(m => !string.IsNullOrWhiteSpace(m.InstrumentId) && m.Price > 0 &&
                        m.AsOfUtc.Kind == DateTimeKind.Utc &&
                        m.AsOfUtc <= asOfUtc && asOfUtc - m.AsOfUtc <= maxMarkAge)
            .GroupBy(m => m.InstrumentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AsOfUtc).First(),
                StringComparer.Ordinal);

        var rows = new List<MarkedHolding>();
        var missing = new List<string>();
        decimal aggregateBasis = 0m;
        decimal realized = 0m;
        decimal markedValue = 0m;
        decimal markedUnrealized = 0m;
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in holdings)
        {
            if (string.IsNullOrWhiteSpace(h.InstrumentId) || !symbols.Add(h.InstrumentId))
                throw new ArgumentException("Each holding must have a unique canonical instrument ID.", nameof(holdings));
            if (h.Quantity < 0 || h.CostBasis < 0 ||
                (h.Quantity == 0 && h.CostBasis != 0) ||
                (h.Quantity > 0 && h.CostBasis <= 0))
                throw new ArgumentException("Invalid long-only stock holding.", nameof(holdings));

            aggregateBasis = checked(aggregateBasis + h.CostBasis);
            realized = checked(realized + h.RealizedPnl);
            var avgCost = h.Quantity == 0 ? 0m : h.CostBasis / h.Quantity;
            if (h.Quantity == 0)
            {
                rows.Add(new MarkedHolding(h.InstrumentId, 0, 0m, 0m, h.RealizedPnl,
                    null, null, 0m, 0m));
            }
            else if (validMarks.TryGetValue(h.InstrumentId, out var mark))
            {
                var value = checked(mark.Price * h.Quantity);
                var unrealized = checked(value - h.CostBasis);
                markedValue = checked(markedValue + value);
                markedUnrealized = checked(markedUnrealized + unrealized);
                rows.Add(new MarkedHolding(h.InstrumentId, h.Quantity, h.CostBasis,
                    avgCost, h.RealizedPnl, mark.Price, mark.AsOfUtc, value, unrealized));
            }
            else
            {
                missing.Add(h.InstrumentId);
                rows.Add(new MarkedHolding(h.InstrumentId, h.Quantity, h.CostBasis,
                    avgCost, h.RealizedPnl, null, null, null, null));
            }
        }

        // Partial marks are NEVER presented as full portfolio value or full NAV.
        var complete = missing.Count == 0;
        return new Snapshot(cashBalance, aggregateBasis, realized,
            complete ? markedValue : null,
            complete ? checked(cashBalance + markedValue) : null,
            complete ? markedUnrealized : null,
            missing.AsReadOnly(), rows.AsReadOnly());
    }
}
