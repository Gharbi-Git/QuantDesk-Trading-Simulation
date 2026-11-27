using Grpc.Net.Client;
using QuantDesk.Contracts.V1.Exchange;
using Stub = QuantDesk.Contracts.V1.Exchange.ExchangeService.ExchangeServiceClient;

namespace QuantDesk.Exchange.Client;

/// <summary>Reusable, development-only plaintext IPv4 loopback client.
/// Keep one instance per configured epoch. Never adopt an epoch discovered from status.
/// No application-level retry policy; failures can have ambiguous outcomes.</summary>
public sealed class GrpcExchangeClient : IExchangeClient, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Stub _stub;
    private readonly TimeSpan _timeout;
    public Guid JournalEpoch { get; }
    private string Epoch => JournalEpoch.ToString("D");

    public GrpcExchangeClient(int loopbackPort, Guid expectedEpoch, TimeSpan? timeout = null)
    {
        if (loopbackPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(loopbackPort));
        ExchangeWire.ValidateEpoch(expectedEpoch);
        JournalEpoch = expectedEpoch;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{loopbackPort}", new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 1024 * 1024,
            MaxSendMessageSize = 64 * 1024
        });
        _stub = new Stub(_channel);
    }

    public async Task<StatusReply> GetStatusAsync(CancellationToken ct = default)
    {
        var result = await _stub.GetStatusAsync(new StatusRequest(), deadline: Deadline(), cancellationToken: ct);
        if (!result.Healthy || result.JournalEpoch != Epoch || result.HighWatermark < 0)
            throw new InvalidDataException("Unhealthy exchange, unexpected epoch, or invalid watermark.");
        return result;
    }

    public async Task<AcceptedReceipt> RegisterBookAsync(Guid commandId, BookKey book, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        var copy = book.Clone();
        ExchangeWire.ValidateBook(copy);
        var result = await _stub.RegisterBookAsync(new RegisterBookRequest { Identity = Identity(commandId), Book = copy }, deadline: Deadline(), cancellationToken: ct);
        return CheckReceipt(result, commandId);
    }

    public async Task<AcceptedReceipt> SubmitOrderAsync(Guid commandId, EquityLimitOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var copy = order.Clone();
        ExchangeWire.ValidateOrder(copy);
        var result = await _stub.SubmitOrderAsync(new SubmitOrderRequest { Identity = Identity(commandId), Order = copy }, deadline: Deadline(), cancellationToken: ct);
        return CheckReceipt(result, commandId);
    }

    public async Task<AcceptedReceipt> CancelOrderAsync(Guid commandId, BookKey book, Guid orderId, Guid accountId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        var copy = book.Clone();
        ExchangeWire.ValidateBook(copy);
        var result = await _stub.CancelOrderAsync(new CancelOrderRequest
        {
            Identity = Identity(commandId), Book = copy,
            OrderId = ExchangeWire.Id(orderId), AccountId = ExchangeWire.Id(accountId)
        }, deadline: Deadline(), cancellationToken: ct);
        return CheckReceipt(result, commandId);
    }

    public async Task<AcceptedReceipt?> GetCommandReceiptAsync(Guid commandId, CancellationToken ct = default)
    {
        var result = await _stub.GetCommandReceiptAsync(new ReceiptRequest { Identity = Identity(commandId) }, deadline: Deadline(), cancellationToken: ct);
        // Absence is NOT evidence of a durable rejection or permission to release funds.
        return result.Receipt is null ? null : CheckReceipt(result.Receipt, commandId);
    }

    public async Task<DeliveryBatch> GetDeliveryBatchAsync(long afterSequence, uint maxCommands = 64, CancellationToken ct = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maxCommands is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxCommands));
        var result = await _stub.GetDeliveryBatchAsync(new DeliveryRequest
        {
            ExpectedJournalEpoch = Epoch, AfterCommandSequence = afterSequence, MaxCommands = maxCommands
        }, deadline: Deadline(), cancellationToken: ct);
        ExchangeWire.ValidateDelivery(result, JournalEpoch, afterSequence, maxCommands);
        return result;
    }

    private CommandIdentity Identity(Guid commandId) => new() { ExpectedJournalEpoch = Epoch, CommandId = ExchangeWire.Id(commandId) };
    private DateTime Deadline() => DateTime.UtcNow.Add(_timeout);
    private AcceptedReceipt CheckReceipt(AcceptedReceipt receipt, Guid commandId)
    {
        if (receipt.JournalEpoch != Epoch || receipt.CommandId != ExchangeWire.Id(commandId) || receipt.CommandSequence <= 0)
            throw new InvalidDataException("Invalid exchange receipt; reconcile the original command, do not release assets.");
        return receipt;
    }
    public void Dispose() => _channel.Dispose();
}
