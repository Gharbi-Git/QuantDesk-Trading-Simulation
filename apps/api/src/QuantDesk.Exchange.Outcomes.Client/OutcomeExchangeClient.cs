using Grpc.Net.Client;
using QuantDesk.Exchange.Client;
using W = QuantDesk.Contracts.V1.Exchange;
using V = QuantDesk.Contracts.V2.Exchange;

namespace QuantDesk.Exchange.Outcomes.Client;

// Explicit V2 API. Never convert a rejected decision to a V1 AcceptedReceipt.
// Trusted, plaintext loopback development transport only.
public sealed class OutcomeExchangeClient : IDisposable
{
    private readonly GrpcChannel channel;
    private readonly V.ExchangeService.ExchangeServiceClient stub;
    private readonly TimeSpan timeout;
    public Guid JournalEpoch { get; }
    private string Epoch => ExchangeWire.Id(JournalEpoch);
    public OutcomeExchangeClient(int port, Guid expectedEpoch, TimeSpan? timeout = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ExchangeWire.ValidateEpoch(expectedEpoch); JournalEpoch = expectedEpoch;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
        channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 1024 * 1024, MaxSendMessageSize = 64 * 1024 });
        stub = new(channel);
    }
    private DateTime Deadline() => DateTime.UtcNow.Add(timeout);
    private W.CommandIdentity Identity(Guid id) => new() { ExpectedJournalEpoch = Epoch, CommandId = ExchangeWire.Id(id) };
    public async Task<V.StatusReply> GetStatusAsync(CancellationToken ct = default)
    {
        var x = await stub.GetStatusAsync(new W.StatusRequest(), deadline: Deadline(), cancellationToken: ct);
        if (!x.Healthy || x.JournalEpoch != Epoch || x.HighWatermark < 0 || x.JournalFormat != 4)
            throw new InvalidDataException("Unhealthy V4 exchange, wrong epoch or format.");
        return x;
    }
    public async Task<V.CommandReceipt> RegisterBookAsync(Guid id, W.BookKey book, CancellationToken ct = default)
    {
        var b = book.Clone(); ExchangeWire.ValidateBook(b);
        var r = await stub.RegisterBookAsync(new W.RegisterBookRequest { Identity = Identity(id), Book = b }, deadline: Deadline(), cancellationToken: ct);
        OutcomeWire.ValidateReceipt(r, JournalEpoch, id);
        var actual = r.Record.Accepted?.Registered?.Book ?? r.Record.Rejected?.RejectedRegistration?.Book;
        if (actual is null || !actual.Equals(b)) throw new InvalidDataException("Registration payload mismatch.");
        return r;
    }
    public async Task<V.CommandReceipt> SubmitOrderAsync(Guid id, W.EquityLimitOrder order, CancellationToken ct = default)
    {
        var copy = order.Clone(); ExchangeWire.ValidateOrder(copy);
        var r = await stub.SubmitOrderAsync(new W.SubmitOrderRequest { Identity = Identity(id), Order = copy }, deadline: Deadline(), cancellationToken: ct);
        OutcomeWire.ValidateReceipt(r, JournalEpoch, id);
        OutcomeWire.RequireSubmission(r.Record, copy);
        return r;
    }
    public async Task<V.CommandReceipt> CancelOrderAsync(Guid id, W.BookKey book, Guid orderId, Guid accountId, CancellationToken ct = default)
    {
        var b = book.Clone(); ExchangeWire.ValidateBook(b);
        var order = ExchangeWire.Id(orderId); var account = ExchangeWire.Id(accountId);
        var r = await stub.CancelOrderAsync(new W.CancelOrderRequest { Identity = Identity(id), Book = b, OrderId = order, AccountId = account }, deadline: Deadline(), cancellationToken: ct);
        OutcomeWire.ValidateReceipt(r, JournalEpoch, id);
        var actual = r.Record.Accepted?.Cancelled ?? r.Record.Rejected?.RejectedCancel;
        if (actual is null || !actual.Book.Equals(b) || actual.OrderId != order || actual.AccountId != account)
            throw new InvalidDataException("Cancellation payload mismatch.");
        return r;
    }
    // A lookup returns the original immutable payload. The caller must compare
    // it with its own durable intent before applying any financial effect.
    public async Task<V.CommandReceipt?> GetCommandReceiptAsync(Guid id, CancellationToken ct = default)
    {
        var r = await stub.GetCommandReceiptAsync(new W.ReceiptRequest { Identity = Identity(id) }, deadline: Deadline(), cancellationToken: ct);
        if (r.Receipt is not null) OutcomeWire.ValidateReceipt(r.Receipt, JournalEpoch, id);
        return r.Receipt;
    }
    public async Task<V.DeliveryBatch> GetDeliveryBatchAsync(long after, uint maxCommands = 64, CancellationToken ct = default)
    {
        if (after < 0 || maxCommands is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(after));
        var r = await stub.GetDeliveryBatchAsync(new W.DeliveryRequest { ExpectedJournalEpoch = Epoch, AfterCommandSequence = after, MaxCommands = maxCommands }, deadline: Deadline(), cancellationToken: ct);
        OutcomeWire.ValidateDelivery(r, JournalEpoch, after, maxCommands);
        return r;
    }
    public void Dispose() => channel.Dispose();
}

public static class OutcomeWire
{
    public static long Sequence(V.DecisionRecord r) => r.DecisionCase switch
    {
        V.DecisionRecord.DecisionOneofCase.Accepted => r.Accepted.CommandSequence,
        V.DecisionRecord.DecisionOneofCase.Rejected => r.Rejected.CommandSequence,
        _ => throw new InvalidDataException("Missing decision.")
    };
    public static string CommandId(V.DecisionRecord r) => r.DecisionCase switch
    {
        V.DecisionRecord.DecisionOneofCase.Accepted => r.Accepted.CommandId,
        V.DecisionRecord.DecisionOneofCase.Rejected => r.Rejected.CommandId,
        _ => throw new InvalidDataException("Missing decision.")
    };
    public static void RequireSubmission(V.DecisionRecord r, W.EquityLimitOrder expected)
    {
        ExchangeWire.ValidateOrder(expected);
        var actual = r.Accepted?.Submitted?.Order ?? r.Rejected?.RejectedSubmit;
        if (actual is null || !actual.Equals(expected)) throw new InvalidDataException("Submission payload mismatch.");
    }
    public static void ValidateReceipt(V.CommandReceipt r, Guid epoch, Guid commandId)
    {
        ExchangeWire.ValidateEpoch(epoch);
        if (r.JournalEpoch != ExchangeWire.Id(epoch) || r.Record is null || CommandId(r.Record) != ExchangeWire.Id(commandId))
            throw new InvalidDataException("Invalid receipt identity.");
        ValidateRecord(r.Record, epoch, Sequence(r.Record));
    }
    public static void ValidateDelivery(V.DeliveryBatch b, Guid epoch, long after, uint maxCommands)
    {
        ExchangeWire.ValidateEpoch(epoch);
        if (after < 0 || maxCommands is < 1 or > 256 || b.JournalEpoch != ExchangeWire.Id(epoch) || b.HighWatermark < after || b.Records.Count != Math.Min((long)maxCommands, b.HighWatermark - after))
            throw new InvalidDataException("Incomplete page, epoch or watermark mismatch.");
        var seen = new HashSet<Guid>();
        foreach (var r in b.Records)
        {
            ValidateRecord(r, epoch, ++after);
            if (!seen.Add(ExchangeWire.ParseId(CommandId(r)))) throw new InvalidDataException("Duplicate command in page.");
        }
    }
    private static void ValidateRecord(V.DecisionRecord r, Guid epoch, long seq)
    {
        if (seq <= 0 || Sequence(r) != seq) throw new InvalidDataException("Missing or noncontiguous sequence.");
        ExchangeWire.ParseId(CommandId(r));
        if (r.DecisionCase == V.DecisionRecord.DecisionOneofCase.Accepted)
        {
            // Reuse all V1 fill, UUIDv5, timestamp and quantity invariants.
            var one = new W.DeliveryBatch { JournalEpoch = ExchangeWire.Id(epoch), HighWatermark = seq };
            one.Records.Add(r.Accepted);
            ExchangeWire.ValidateDelivery(one, epoch, seq - 1, 1);
            return;
        }
        var rejected = r.Rejected;
        if (rejected.Reason is not (V.RejectionReason.DomainRejected or V.RejectionReason.NumericRejected))
            throw new InvalidDataException("Unknown rejection reason.");
        switch (rejected.PayloadCase)
        {
            case V.RejectedDecision.PayloadOneofCase.RejectedRegistration: ExchangeWire.ValidateBook(rejected.RejectedRegistration.Book); break;
            case V.RejectedDecision.PayloadOneofCase.RejectedSubmit: ExchangeWire.ValidateOrder(rejected.RejectedSubmit); break;
            case V.RejectedDecision.PayloadOneofCase.RejectedCancel:
                ExchangeWire.ValidateBook(rejected.RejectedCancel.Book); ExchangeWire.ParseId(rejected.RejectedCancel.OrderId); ExchangeWire.ParseId(rejected.RejectedCancel.AccountId); break;
            default: throw new InvalidDataException("Missing rejected payload.");
        }
    }
}
