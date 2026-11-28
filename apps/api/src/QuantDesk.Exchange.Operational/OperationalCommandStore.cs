using Google.Protobuf;
using Npgsql;
using NpgsqlTypes;
using QuantDesk.Exchange.Client;
using QuantDesk.Exchange.Outcomes.Client;
using W = QuantDesk.Contracts.V1.Exchange;
using V = QuantDesk.Contracts.V2.Exchange;

namespace QuantDesk.Exchange.Operational;

public sealed record StoredCommand(Guid Id, Guid Epoch, string Kind, Guid? Owner, Guid? OrderId,
    Guid? Target, string? Admission, string? RiskVersion, byte[] Payload, string Phase, long? Sequence, byte[]? Decision);
public sealed record JournalBinding(Guid Epoch, long ObservedHead, long Cursor);

// Trusted persistence primitive, not a player API or a financial authorization service.
// Caller owns transaction lifetime, must authenticate status/receipts, verify M07
// reservation before marking reserved, and commit all M07 effects before advancing.
// No runtime DDL, no implicit journal binding, no network calls under DB locks.
public sealed class OperationalCommandStore(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    private NpgsqlCommand Sql(string text) => new(text, connection, transaction);
    private static void Param(NpgsqlCommand q, string name, NpgsqlDbType type, object? value) =>
        q.Parameters.Add(name, type).Value = value ?? DBNull.Value;
    private static W.CommandIdentity Identity(Guid epoch, Guid id)
    {
        ExchangeWire.ValidateEpoch(epoch);
        return new() { ExpectedJournalEpoch = ExchangeWire.Id(epoch), CommandId = ExchangeWire.Id(id) };
    }
    public async Task BindEmptyAsync(V.StatusReply status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        var epoch = ExchangeWire.ParseId(status.JournalEpoch); ExchangeWire.ValidateEpoch(epoch);
        await using var q = Sql("SELECT qd_exchange_v4.bind_empty_journal(@epoch,@format,@healthy,@head)");
        StatusParams(q, status, epoch); await q.ExecuteNonQueryAsync(ct);
    }
    public async Task<long> ObserveAsync(V.StatusReply status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        var epoch = ExchangeWire.ParseId(status.JournalEpoch); ExchangeWire.ValidateEpoch(epoch);
        await using var q = Sql("SELECT qd_exchange_v4.observe_engine(@epoch,@format,@healthy,@head)");
        StatusParams(q, status, epoch); return (long)(await q.ExecuteScalarAsync(ct))!;
    }
    private static void StatusParams(NpgsqlCommand q, V.StatusReply status, Guid epoch)
    {
        q.Parameters.AddWithValue("epoch", epoch); q.Parameters.AddWithValue("format", checked((int)status.JournalFormat));
        q.Parameters.AddWithValue("healthy", status.Healthy); q.Parameters.AddWithValue("head", status.HighWatermark);
    }
    public async Task<JournalBinding> GetBindingAsync(CancellationToken ct = default)
    {
        await using var q = Sql("SELECT journal_epoch,observed_head,last_command_sequence FROM qd_exchange_v4.journal_binding WHERE singleton");
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("No operational journal binding.");
        return new(r.GetGuid(0),r.GetInt64(1),r.GetInt64(2));
    }
    public Task PrepareRegistrationAsync(Guid epoch, Guid id, W.BookKey book, CancellationToken ct = default)
    {
        var b = book.Clone(); ExchangeWire.ValidateBook(b);
        return InsertAsync(id, epoch, "register", null,null,null,null,null,
            new W.RegisterBookRequest { Identity = Identity(epoch,id), Book = b }.ToByteArray(), ct);
    }
    public Task PrepareSubmissionAsync(Guid epoch, Guid id, Guid owner, W.EquityLimitOrder order,
        string admissionReference, string riskVersion, CancellationToken ct = default)
    {
        ExchangeWire.Id(owner); var o = order.Clone(); ExchangeWire.ValidateOrder(o);
        if (string.IsNullOrWhiteSpace(admissionReference) || admissionReference.Length > 256 || string.IsNullOrWhiteSpace(riskVersion) || riskVersion.Length > 128)
            throw new ArgumentException("Admission reference and risk policy version required.");
        return InsertAsync(id,epoch,"submit",owner,ExchangeWire.ParseId(o.OrderId),null,admissionReference,riskVersion,
            new W.SubmitOrderRequest { Identity = Identity(epoch,id), Order = o }.ToByteArray(),ct);
    }
    public async Task PrepareCancellationAsync(Guid epoch, Guid id, Guid owner, Guid targetSubmissionId, CancellationToken ct = default)
    {
        ExchangeWire.Id(owner); var s = await ReadAsync(targetSubmissionId,ct);
        if (s.Epoch != epoch || s.Owner != owner || s.Kind != "submit" || s.Phase != "accepted")
            throw new InvalidOperationException("Cancellation requires accepted owned submission.");
        var o = W.SubmitOrderRequest.Parser.ParseFrom(s.Payload).Order; ExchangeWire.ValidateOrder(o);
        await InsertAsync(id,epoch,"cancel",owner,ExchangeWire.ParseId(o.OrderId),targetSubmissionId,null,null,
            new W.CancelOrderRequest { Identity = Identity(epoch,id), Book = o.Book.Clone(), OrderId = o.OrderId, AccountId = o.AccountId }.ToByteArray(),ct);
    }
    private async Task InsertAsync(Guid id, Guid epoch, string kind, Guid? owner, Guid? order, Guid? target, string? admission, string? risk, byte[] payload, CancellationToken ct)
    {
        await using var q = Sql("""
            INSERT INTO qd_exchange_v4.commands(command_id,journal_epoch,kind,owner_user_id,order_id,target_submission_id,admission_reference,risk_policy_version,payload)
            VALUES (@id,@epoch,@kind,@owner,@order,@target,@admission,@risk,@payload) ON CONFLICT(command_id) DO NOTHING
            """);
        q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("epoch",epoch); q.Parameters.AddWithValue("kind",kind);
        Param(q,"owner",NpgsqlDbType.Uuid,owner); Param(q,"order",NpgsqlDbType.Uuid,order); Param(q,"target",NpgsqlDbType.Uuid,target);
        Param(q,"admission",NpgsqlDbType.Text,admission); Param(q,"risk",NpgsqlDbType.Text,risk); q.Parameters.AddWithValue("payload",payload);
        await q.ExecuteNonQueryAsync(ct);
        var r = await ReadAsync(id,ct);
        if (r.Epoch != epoch || r.Kind != kind || r.Owner != owner || r.OrderId != order || r.Target != target ||
            r.Admission != admission || r.RiskVersion != risk || !r.Payload.AsSpan().SequenceEqual(payload))
            throw new InvalidOperationException("Conflicting durable intent.");
    }
    private const string Columns = "command_id,journal_epoch,kind,owner_user_id,order_id,target_submission_id,admission_reference,risk_policy_version,payload,phase,decision_sequence,decision_payload";
    public async Task<StoredCommand> ReadAsync(Guid id, CancellationToken ct = default)
    {
        await using var q = Sql($"SELECT {Columns} FROM qd_exchange_v4.commands WHERE command_id=@id FOR UPDATE");
        q.Parameters.AddWithValue("id",id); await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Unknown durable command.");
        return ReadRow(r);
    }
    public async Task<IReadOnlyList<StoredCommand>> PendingAsync(Guid epoch, int limit = 64, CancellationToken ct = default)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var q = Sql($"SELECT {Columns} FROM qd_exchange_v4.commands WHERE journal_epoch=@epoch AND phase IN ('pending','reserved') ORDER BY created_at,command_id LIMIT @limit");
        q.Parameters.AddWithValue("epoch",epoch);q.Parameters.AddWithValue("limit",limit);
        var result = new List<StoredCommand>(); await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(ReadRow(r)); return result;
    }
    private static StoredCommand ReadRow(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),
        r.IsDBNull(3)?null:r.GetGuid(3),r.IsDBNull(4)?null:r.GetGuid(4),r.IsDBNull(5)?null:r.GetGuid(5),
        r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetFieldValue<byte[]>(8),r.GetString(9),
        r.IsDBNull(10)?null:r.GetInt64(10),r.IsDBNull(11)?null:r.GetFieldValue<byte[]>(11));
    // This marker is NOT a reservation operation. Only trusted M07 orchestration may call it.
    public async Task MarkReservationConfirmedAsync(Guid id, CancellationToken ct = default)
    {
        var r = await ReadAsync(id,ct);
        if (r.Kind != "submit") throw new InvalidOperationException("Only submissions require reservations.");
        if (r.Phase == "reserved") return;
        if (r.Phase != "pending") throw new InvalidOperationException("Submission already decided.");
        await using var q = Sql("UPDATE qd_exchange_v4.commands SET phase='reserved' WHERE command_id=@id AND phase='pending'");
        q.Parameters.AddWithValue("id",id); if (await q.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Reservation marker conflict.");
    }
    // Both receipt and one-record delivery page must originate from the trusted exchange.
    public async Task RecordDecisionAsync(Guid id, V.CommandReceipt receipt, V.DeliveryBatch page, CancellationToken ct = default)
    {
        var r = await ReadAsync(id,ct);
        OutcomeWire.ValidateReceipt(receipt,r.Epoch,id); var record = receipt.Record;
        var seq = OutcomeWire.Sequence(record); OutcomeWire.ValidateDelivery(page,r.Epoch,seq-1,1);
        if (!page.Records.Single().Equals(record)) throw new InvalidDataException("Receipt differs from delivery.");
        switch (r.Kind)
        {
            case "submit": OutcomeWire.RequireSubmission(record,W.SubmitOrderRequest.Parser.ParseFrom(r.Payload).Order); break;
            case "register":
                var book = W.RegisterBookRequest.Parser.ParseFrom(r.Payload).Book;
                var actualBook = record.Accepted?.Registered?.Book ?? record.Rejected?.RejectedRegistration?.Book;
                if (actualBook is null || !actualBook.Equals(book)) throw new InvalidDataException("Registration payload mismatch."); break;
            case "cancel":
                var expected = W.CancelOrderRequest.Parser.ParseFrom(r.Payload);
                var actual = record.Accepted?.Cancelled ?? record.Rejected?.RejectedCancel;
                if (actual is null || !actual.Book.Equals(expected.Book) || actual.OrderId != expected.OrderId || actual.AccountId != expected.AccountId)
                    throw new InvalidDataException("Cancellation payload mismatch."); break;
            default: throw new InvalidDataException("Unknown command kind.");
        }
        var phase = record.Accepted is null ? "rejected" : "accepted"; var bytes = record.ToByteArray();
        if (r.Phase is "accepted" or "rejected")
        {
            if (r.Phase != phase || r.Sequence != seq || r.Decision is null || !V.DecisionRecord.Parser.ParseFrom(r.Decision).Equals(record))
                throw new InvalidDataException("Immutable decision conflict."); return;
        }
        if (r.Phase != (r.Kind=="submit"?"reserved":"pending")) throw new InvalidOperationException("Command is not ready for decision recording.");
        await using var q = Sql("UPDATE qd_exchange_v4.commands SET phase=@phase,decision_sequence=@seq,decision_payload=@payload WHERE command_id=@id");
        q.Parameters.AddWithValue("phase",phase);q.Parameters.AddWithValue("seq",seq);q.Parameters.AddWithValue("payload",bytes);q.Parameters.AddWithValue("id",id);
        if(await q.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Decision update conflict.");
    }
    // Caller must prove all M07 effects committed before calling this method.
    public async Task AdvanceAfterEffectsAsync(Guid epoch,long previous,long next,CancellationToken ct=default)
    {
        await using var q=Sql("SELECT qd_exchange_v4.advance_checkpoint(@epoch,@previous,@next)");
        q.Parameters.AddWithValue("epoch",epoch);q.Parameters.AddWithValue("previous",previous);q.Parameters.AddWithValue("next",next);await q.ExecuteNonQueryAsync(ct);
    }
}
