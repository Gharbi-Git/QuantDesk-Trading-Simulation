using QuantDesk.Contracts.V1.Exchange;

namespace QuantDesk.Exchange.Client;

/// <summary>Trusted internal transport only. An accepted receipt is not settlement.
/// Callers must durably store command ID, epoch and exact request before sending.
/// No automatic reservation release on errors or missing receipts.</summary>
public interface IExchangeClient
{
    Guid JournalEpoch { get; }
    Task<StatusReply> GetStatusAsync(CancellationToken ct = default);
    Task<AcceptedReceipt> RegisterBookAsync(Guid commandId, BookKey book, CancellationToken ct = default);
    Task<AcceptedReceipt> SubmitOrderAsync(Guid commandId, EquityLimitOrder order, CancellationToken ct = default);
    Task<AcceptedReceipt> CancelOrderAsync(Guid commandId, BookKey book, Guid orderId, Guid accountId, CancellationToken ct = default);
    Task<AcceptedReceipt?> GetCommandReceiptAsync(Guid commandId, CancellationToken ct = default);
    Task<DeliveryBatch> GetDeliveryBatchAsync(long afterSequence, uint maxCommands = 64, CancellationToken ct = default);
}
