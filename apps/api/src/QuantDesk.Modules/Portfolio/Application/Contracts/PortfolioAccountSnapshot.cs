namespace QuantDesk.Modules.Portfolio.Application.Contracts;

/// <summary>Provisional in-process account read model, not a frozen shared wire contract.
/// Version tracks accounting and reservations; marks can age without a version change.
/// CashBalance includes reserved cash. NAV includes all owned shares, even reserved ones.</summary>
public sealed record PortfolioAccountSnapshot(
    Guid AccountId, string Currency, long Version, DateTime AsOfUtc,
    decimal CashBalance, decimal ReservedCash, decimal AvailableCash,
    decimal TotalCostBasis, decimal RealizedPnl, decimal? MarketValue,
    decimal? Nav, decimal? UnrealizedPnl, string MarkPolicy,
    IReadOnlyList<string> MissingMarkInstruments,
    IReadOnlyList<PortfolioPositionSnapshot> Positions);

public sealed record PortfolioPositionSnapshot(
    string InstrumentId, long Quantity, long ReservedShares, long AvailableShares,
    decimal CostBasis, decimal AverageAcquisitionCost, decimal RealizedPnl,
    decimal? MarkPrice, DateTime? MarkAsOfUtc, decimal? MarketValue, decimal? UnrealizedPnl);
