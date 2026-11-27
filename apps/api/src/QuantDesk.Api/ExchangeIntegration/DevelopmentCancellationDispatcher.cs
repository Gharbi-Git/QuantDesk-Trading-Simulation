using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Contracts.V1.Exchange;
using QuantDesk.Exchange.Client;
using QuantDesk.Modules.Infrastructure.Persistence;

namespace QuantDesk.Api.ExchangeIntegration;

/// <summary>Trusted test-only cancellation. An accepted cancel does not release
/// assets; only the ordered, authorized consumer may release the remainder.</summary>
public sealed class DevelopmentCancellationDispatcher
{
    private readonly IExchangeClient _exchange;
    private readonly string _connectionString;
    public DevelopmentCancellationDispatcher(IExchangeClient exchange, DbContextOptions<QuantDeskDbContext> options)
    {
        _exchange = exchange;
        ExchangeWire.ValidateEpoch(exchange.JournalEpoch);
        using var db = new QuantDeskDbContext(options);
        if (db.Database.GetDbConnection() is not NpgsqlConnection c || c.Database != "quantdesk_test")
            throw new InvalidOperationException("Development cancellation requires quantdesk_test.");
        _connectionString = c.ConnectionString;
    }

    public async Task ProvisionTestTableAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var q = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS qd_exchange_dev_cancellations (
                command_id uuid PRIMARY KEY, journal_epoch uuid NOT NULL,
                owner_user_id uuid NOT NULL, order_id uuid NOT NULL UNIQUE,
                accepted_sequence bigint CHECK (accepted_sequence > 0),
                UNIQUE(journal_epoch, accepted_sequence)
            )
            """, c);
        await q.ExecuteNonQueryAsync(ct);
    }

    // The accepted submission is the authoritative payload. Do not accept an
    // arbitrary account, session, or instrument from a cancellation caller.
    public async Task PrepareAsync(Guid ownerUserId, Guid commandId, Guid orderId, CancellationToken ct = default)
    {
        ExchangeWire.Id(ownerUserId); ExchangeWire.Id(commandId); ExchangeWire.Id(orderId);
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var q = new NpgsqlCommand("""
            SELECT s.payload FROM qd_exchange_dev_submissions s
            WHERE s.order_id=@order AND s.journal_epoch=@epoch AND s.owner_user_id=@owner AND s.phase=2
            FOR UPDATE
            """, c, tx))
        {
            q.Parameters.AddWithValue("order", orderId); q.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
            q.Parameters.AddWithValue("owner", ownerUserId);
            if (await q.ExecuteScalarAsync(ct) is not byte[] bytes)
                throw new InvalidOperationException("No accepted submission for cancellation owner.");
            var order = EquityLimitOrder.Parser.ParseFrom(bytes);
            ExchangeWire.ValidateOrder(order);
            if (ExchangeWire.ParseId(order.OrderId) != orderId) throw new InvalidDataException("Corrupt submission identity.");
            await VerifyOwnerAsync(c, tx, ownerUserId, order, ct);
        }
        await using (var q = new NpgsqlCommand("""
            INSERT INTO qd_exchange_dev_cancellations(command_id,journal_epoch,owner_user_id,order_id)
            VALUES (@id,@epoch,@owner,@order) ON CONFLICT(command_id) DO NOTHING
            """, c, tx))
        {
            q.Parameters.AddWithValue("id", commandId); q.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
            q.Parameters.AddWithValue("owner", ownerUserId); q.Parameters.AddWithValue("order", orderId);
            await q.ExecuteNonQueryAsync(ct);
        }
        var row = await ReadAsync(c, tx, commandId, ct);
        if (row.Owner != ownerUserId || ExchangeWire.ParseId(row.Order.OrderId) != orderId)
            throw new InvalidOperationException("Cancellation owner or order conflict.");
        await tx.CommitAsync(ct);
    }

    public async Task<AcceptedReceipt> DispatchAsync(Guid commandId, CancellationToken ct = default)
    {
        ExchangeWire.Id(commandId);
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await ReadAsync(c, tx, commandId, ct);
        await VerifyOwnerAsync(c, tx, row.Owner, row.Order, ct);
        var receipt = await _exchange.GetCommandReceiptAsync(commandId, ct);
        if (row.Sequence.HasValue && (receipt is null || receipt.CommandSequence != row.Sequence))
            throw new InvalidOperationException("Previously accepted cancellation missing or changed.");
        // No receipt or RPC error is never permission to release assets.
        receipt ??= await _exchange.CancelOrderAsync(commandId, row.Order.Book.Clone(),
            ExchangeWire.ParseId(row.Order.OrderId), ExchangeWire.ParseId(row.Order.AccountId), ct);
        if (receipt.CommandId != ExchangeWire.Id(commandId) || receipt.JournalEpoch != ExchangeWire.Id(_exchange.JournalEpoch) ||
            receipt.CommandSequence <= row.SubmissionSequence)
            throw new InvalidDataException("Invalid cancellation receipt.");
        var batch = await _exchange.GetDeliveryBatchAsync(receipt.CommandSequence - 1, 1, ct);
        ExchangeWire.ValidateDelivery(batch, _exchange.JournalEpoch, receipt.CommandSequence - 1, 1);
        var record = batch.Records.Single();
        if (record.CommandId != receipt.CommandId || record.EventCase != DeliveryRecord.EventOneofCase.Cancelled ||
            !record.Cancelled.Book.Equals(row.Order.Book) || record.Cancelled.OrderId != row.Order.OrderId ||
            record.Cancelled.AccountId != row.Order.AccountId)
            throw new InvalidDataException("Cancellation differs from durable intent.");
        if (!row.Sequence.HasValue)
        {
            await using var q = new NpgsqlCommand("UPDATE qd_exchange_dev_cancellations SET accepted_sequence=@seq WHERE command_id=@id AND accepted_sequence IS NULL", c, tx);
            q.Parameters.AddWithValue("seq", receipt.CommandSequence); q.Parameters.AddWithValue("id", commandId);
            if (await q.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Unexpected cancellation transition.");
        }
        await tx.CommitAsync(ct);
        return receipt;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connectionString);
        try { await c.OpenAsync(ct); return c; }
        catch { await c.DisposeAsync(); throw; }
    }
    private static async Task VerifyOwnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid owner, EquityLimitOrder order, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM comptes_trading WHERE \"Id\"=@account AND owner_user_id=@owner)", c, tx);
        q.Parameters.AddWithValue("account", ExchangeWire.ParseId(order.AccountId)); q.Parameters.AddWithValue("owner", owner);
        if (await q.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Trading account not found for owner.");
    }
    private async Task<Intent> ReadAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("""
            SELECT c.journal_epoch,c.owner_user_id,c.order_id,c.accepted_sequence,s.payload,s.accepted_sequence
            FROM qd_exchange_dev_cancellations c JOIN qd_exchange_dev_submissions s ON s.order_id=c.order_id
            WHERE c.command_id=@id AND s.journal_epoch=c.journal_epoch AND s.owner_user_id=c.owner_user_id AND s.phase=2
            FOR UPDATE OF c
            """, c, tx);
        q.Parameters.AddWithValue("id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Unknown durable cancellation.");
        if (r.GetGuid(0) != _exchange.JournalEpoch) throw new InvalidOperationException("Journal epoch conflict.");
        var order = EquityLimitOrder.Parser.ParseFrom(r.GetFieldValue<byte[]>(4));
        ExchangeWire.ValidateOrder(order);
        if (ExchangeWire.ParseId(order.OrderId) != r.GetGuid(2)) throw new InvalidDataException("Corrupt cancellation identity.");
        return new(r.GetGuid(1),order,r.IsDBNull(3) ? null : r.GetInt64(3),r.GetInt64(5));
    }
    private sealed record Intent(Guid Owner, EquityLimitOrder Order, long? Sequence, long SubmissionSequence);
}
