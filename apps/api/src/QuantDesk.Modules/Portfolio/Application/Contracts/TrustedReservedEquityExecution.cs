namespace QuantDesk.Modules.Portfolio.Application.Contracts;

/// <summary>Provisional in-process M07 contract for a verified, sequenced equity fill.
/// Not a frozen wire contract. USD limit orders only; no fees.</summary>
public sealed record TrustedReservedEquityExecution(Guid ExecutionId, Guid BuyerAccountId,
    Guid SellerAccountId, string InstrumentId, long Quantity, decimal ExecutionPrice,
    Guid BuyOrderId, Guid SellOrderId, Guid SessionId);
