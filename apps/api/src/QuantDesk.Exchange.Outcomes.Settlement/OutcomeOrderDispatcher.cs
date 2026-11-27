using System.Data;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Contracts.V1.Exchange;
using QuantDesk.Exchange.Client;
using QuantDesk.Exchange.Outcomes.Client;
using V = QuantDesk.Contracts.V2.Exchange;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;

namespace QuantDesk.Exchange.Outcomes.Settlement;

/// <summary>Trusted test-only submission coordinator. Caller must already have
/// authorized session, instrument and risk. Never expose to players. All submits
/// for an isolated test journal must use this coordinator; do not release holds
/// outside the ordered settlement consumer. No background retry; the ordered V4 consumer applies confirmed rejections.</summary>
public sealed class OutcomeOrderDispatcher
{
    private readonly OutcomeExchangeClient _exchange;
    private readonly DbContextOptions<QuantDeskDbContext> _options;
    private readonly PortfolioReservationService _reservations;
    private readonly string _connectionString;
    public OutcomeOrderDispatcher(OutcomeExchangeClient exchange, DbContextOptions<QuantDeskDbContext> options)
    {
        _exchange = exchange;
        _options = options;
        ExchangeWire.ValidateEpoch(exchange.JournalEpoch);
        using var db = new QuantDeskDbContext(options);
        if (db.Database.GetDbConnection() is not NpgsqlConnection c || c.Database != "quantdesk_test")
            throw new InvalidOperationException("Development dispatcher requires quantdesk_test.");
        _connectionString = c.ConnectionString;
        _reservations = new(options);
    }

    // Explicit test provisioning only, no production DDL or EF model changes.
    public async Task ProvisionTestTableAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var sql = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS qd_exchange_v4_dev_submissions (
                command_id uuid PRIMARY KEY, journal_epoch uuid NOT NULL,
                owner_user_id uuid NOT NULL, order_id uuid NOT NULL UNIQUE,
                payload bytea NOT NULL, phase integer NOT NULL CHECK (phase BETWEEN 0 AND 3),
                decision_sequence bigint,
                CHECK ((phase IN (2,3) AND decision_sequence IS NOT NULL AND decision_sequence > 0)
                       OR (phase IN (0,1) AND decision_sequence IS NULL)),
                UNIQUE(journal_epoch, decision_sequence)
            )
            """, c);
        await sql.ExecuteNonQueryAsync(ct);
    }

    // Intent must commit before reservation; a retry must supply exactly the
    // same owner, order, command and epoch. No RPC takes place in PrepareAsync.
    public async Task PrepareAsync(Guid ownerUserId, Guid commandId, EquityLimitOrder request, CancellationToken ct = default)
    {
        ExchangeWire.Id(ownerUserId); ExchangeWire.Id(commandId);
        ArgumentNullException.ThrowIfNull(request);
        var order = request.Clone();
        ExchangeWire.ValidateOrder(order);
        var orderId = ExchangeWire.ParseId(order.OrderId);
        await using var c = await OpenAsync(ct);
        await using (var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO qd_exchange_v4_dev_submissions(command_id,journal_epoch,owner_user_id,order_id,payload,phase)
                VALUES (@id,@epoch,@owner,@order,@payload,0) ON CONFLICT(command_id) DO NOTHING
                """, c, tx);
            insert.Parameters.AddWithValue("id", commandId);
            insert.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
            insert.Parameters.AddWithValue("owner", ownerUserId);
            insert.Parameters.AddWithValue("order", orderId);
            insert.Parameters.AddWithValue("payload", order.ToByteArray());
            var inserted = await insert.ExecuteNonQueryAsync(ct) == 1;
            var prior = await ReadLockedAsync(c, tx, commandId, ct);
            if (prior.Owner != ownerUserId || !prior.Order.Equals(order))
                throw new InvalidOperationException("Command payload or owner conflict.");
            if (inserted)
            {
                // Existing unrelated reservations may not be adopted by a new intent.
                await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM equity_order_reservations WHERE order_id=@order)", c, tx);
                exists.Parameters.AddWithValue("order", orderId);
                if ((bool)(await exists.ExecuteScalarAsync(ct))!)
                    throw new InvalidOperationException("Order already has a reservation outside this dispatcher.");
            }
            await tx.CommitAsync(ct);
        }
        // Independent M07 transaction, serialized by the durable intent row.
        // If reserve commits but this transaction fails, PrepareAsync retries
        // the identical reservation and M07 returns its existing outcome.
        await using var reserveTx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await ReadLockedAsync(c, reserveTx, commandId, ct);
        if (row.Phase == 0)
        {
            var result = await _reservations.ReserveAsync(row.Owner, orderId,
                ExchangeWire.ParseId(order.AccountId), ExchangeWire.ParseId(order.Book.SessionId),
                order.Book.InstrumentId, ReservationSide(order), order.Quantity,
                ExchangeWire.ToUsd(order.LimitPriceUnits), ct);
            if (result.Status != EquityReservationStatus.Active || result.RemainingQuantity != order.Quantity)
                throw new InvalidOperationException("Reservation is not fully active before dispatch.");
            await UpdateAsync(c, reserveTx, commandId, 1, null, ct);
        }
        await reserveTx.CommitAsync(ct);
    }

    // A transport failure, deadline, cancellation or engine error leaves phase 1
    // and the hold intact. Absence of receipt is NOT definitive rejection.
    public async Task<V.CommandReceipt> DispatchAsync(Guid commandId, CancellationToken ct = default)
    {
        ExchangeWire.Id(commandId);
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await ReadLockedAsync(c, tx, commandId, ct);
        if (row.Phase == 0) throw new InvalidOperationException("Reservation not confirmed; dispatch prohibited.");
        var receipt = await _exchange.GetCommandReceiptAsync(commandId, ct);
        if (row.Phase >= 2 && (receipt is null || OutcomeWire.Sequence(receipt.Record) != row.Sequence || (receipt.Record.Accepted is not null) != (row.Phase == 2)))
            throw new InvalidOperationException("Previously recorded decision missing or changed; reconcile journal.");
        if (receipt is null)
        {
            // For an original submission or retry with no known receipt, require
            // exact full reservation. No out-of-band releases are supported.
            await using var db = new QuantDeskDbContext(_options);
            var id = ExchangeWire.ParseId(row.Order.OrderId);
            var hold = await db.EquityOrderReservations.AsNoTracking().SingleOrDefaultAsync(r => r.OrderId == id, ct);
            if (hold is null || hold.AccountId != ExchangeWire.ParseId(row.Order.AccountId) ||
                hold.SessionId != ExchangeWire.ParseId(row.Order.Book.SessionId) || hold.InstrumentId != row.Order.Book.InstrumentId ||
                hold.Side != ReservationSide(row.Order) || hold.OriginalQuantity != row.Order.Quantity ||
                hold.LimitPrice != ExchangeWire.ToUsd(row.Order.LimitPriceUnits) ||
                hold.Status != EquityReservationStatus.Active || hold.RemainingQuantity != row.Order.Quantity)
                throw new InvalidOperationException("Exact active reservation required before RPC.");
            receipt = await _exchange.SubmitOrderAsync(commandId, row.Order.Clone(), ct);
        }
        OutcomeWire.ValidateReceipt(receipt, _exchange.JournalEpoch, commandId);
        OutcomeWire.RequireSubmission(receipt.Record, row.Order);
        var seq = OutcomeWire.Sequence(receipt.Record);
        var batch = await _exchange.GetDeliveryBatchAsync(seq - 1, 1, ct);
        OutcomeWire.ValidateDelivery(batch, _exchange.JournalEpoch, seq - 1, 1);
        if (!batch.Records.Single().Equals(receipt.Record))
            throw new InvalidDataException("Receipt differs from journal delivery.");
        if (row.Phase == 1)
            await UpdateAsync(c, tx, commandId, receipt.Record.Accepted is not null ? 2 : 3, seq, ct);
        await tx.CommitAsync(ct);
        return receipt;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connectionString);
        try { await c.OpenAsync(ct); return c; }
        catch { await c.DisposeAsync(); throw; }
    }
    private async Task<Submission> ReadLockedAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("SELECT journal_epoch, owner_user_id, order_id, payload, phase, decision_sequence FROM qd_exchange_v4_dev_submissions WHERE command_id=@id FOR UPDATE", c, tx);
        q.Parameters.AddWithValue("id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Unknown durable command.");
        if (r.GetGuid(0) != _exchange.JournalEpoch) throw new InvalidOperationException("Journal epoch conflict.");
        var owner = r.GetGuid(1);
        var order = EquityLimitOrder.Parser.ParseFrom(r.GetFieldValue<byte[]>(3));
        ExchangeWire.ValidateOrder(order);
        if (r.GetGuid(2) != ExchangeWire.ParseId(order.OrderId)) throw new InvalidDataException("Corrupt durable order identity.");
        return new Submission(owner, order, r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt64(5));
    }
    private static async Task UpdateAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, int phase, long? seq, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("UPDATE qd_exchange_v4_dev_submissions SET phase=@phase, decision_sequence=@seq WHERE command_id=@id AND phase=@prior", c, tx);
        q.Parameters.AddWithValue("id", id); q.Parameters.AddWithValue("phase", phase); q.Parameters.AddWithValue("prior", phase == 3 ? 1 : phase - 1);
        q.Parameters.Add("seq", NpgsqlTypes.NpgsqlDbType.Bigint).Value = (object?)seq ?? DBNull.Value;
        if (await q.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Unexpected dispatch state transition.");
    }
    private static EquityReservationSide ReservationSide(EquityLimitOrder order) => order.Side == Side.Buy ? EquityReservationSide.Buy : EquityReservationSide.Sell;
    private sealed record Submission(Guid Owner, EquityLimitOrder Order, int Phase, long? Sequence);
}
