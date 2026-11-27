using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Contracts.V1.Exchange;
using QuantDesk.Exchange.Client;
using QuantDesk.Exchange.Outcomes.Client;
using V = QuantDesk.Contracts.V2.Exchange;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;

namespace QuantDesk.Exchange.Outcomes.Settlement;

/// <summary>Explicitly invoked development consumer. Test DB and isolated V4 epoch only.
/// One command at a time under a PostgreSQL checkpoint row lock. M07 commits each
/// effect separately; replays use its durable receipts and idempotent releases.
/// Do not expose via HTTP or run against live accounts.</summary>
public sealed class OutcomeDeliveryConsumer
{
    private readonly OutcomeExchangeClient _exchange;
    private readonly DbContextOptions<QuantDeskDbContext> _options;
    private readonly ReservedEquitySettlementService _settlement;
    private readonly PortfolioReservationService _reservations;

    public OutcomeDeliveryConsumer(OutcomeExchangeClient exchange, DbContextOptions<QuantDeskDbContext> options)
    {
        _exchange = exchange;
        _options = options;
        ExchangeWire.ValidateEpoch(exchange.JournalEpoch);
        using var db = new QuantDeskDbContext(options);
        if (db.Database.GetDbConnection() is not NpgsqlConnection connection ||
            connection.Database != "quantdesk_test")
            throw new InvalidOperationException("Development consumer requires quantdesk_test.");
        _settlement = new(options);
        _reservations = new(options);
    }

    // No auto-provisioning of a cursor. The test harness must create the table
    // and insert a zero cursor for a new empty journal, before sending commands.
    public async Task<DeliveryProgress> ProcessNextAsync(CancellationToken ct = default)
    {
        await using var db = new QuantDeskDbContext(_options);
        await db.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var read = new NpgsqlCommand("SELECT last_command_sequence FROM qd_exchange_v4_dev_checkpoints WHERE journal_epoch = @epoch FOR UPDATE", connection, tx);
        read.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
        if (await read.ExecuteScalarAsync(ct) is not long cursor || cursor < 0)
            throw new InvalidOperationException("No explicitly provisioned checkpoint for this epoch.");

        // Read one complete command, validate the entire message before any effect.
        var batch = await _exchange.GetDeliveryBatchAsync(cursor, 1, ct);
        OutcomeWire.ValidateDelivery(batch, _exchange.JournalEpoch, cursor, 1);
        if (batch.Records.Count == 0)
        {
            await tx.CommitAsync(ct);
            return new(cursor, batch.HighWatermark, false);
        }
        var record = batch.Records[0];
        if (record.Accepted is not null)
        {
            await VerifyAuthorizationAsync(connection, tx, record.Accepted, ct);
            await ApplyAsync(record.Accepted, ct);
        }
        else await ApplyRejectionAsync(connection, tx, record.Rejected, ct);
        var seq = OutcomeWire.Sequence(record);
        // This is deliberately a separate transaction from M07 effects. If it
        // fails after a fill commits, retry that command; never skip its remainder.
        await using var update = new NpgsqlCommand("UPDATE qd_exchange_v4_dev_checkpoints SET last_command_sequence = @next WHERE journal_epoch = @epoch AND last_command_sequence = @previous", connection, tx);
        update.Parameters.AddWithValue("next", seq);
        update.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
        update.Parameters.AddWithValue("previous", cursor);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Checkpoint changed unexpectedly.");
        await tx.CommitAsync(ct);
        return new(seq, batch.HighWatermark, true);
    }

    // Check intent and matching accepted sequence before any financial effect.
    // A lost response can leave the intent pending; recover it through the
    // appropriate dispatcher, then retry this unchanged checkpoint.
    private async Task VerifyAuthorizationAsync(NpgsqlConnection c, NpgsqlTransaction tx, DeliveryRecord record, CancellationToken ct)
    {
        if (record.EventCase == DeliveryRecord.EventOneofCase.Registered) return; // trusted fixture only
        var cancellation = record.EventCase == DeliveryRecord.EventOneofCase.Cancelled;
        var sql = cancellation ? """
            SELECT s.payload FROM qd_exchange_v4_dev_cancellations x
            JOIN qd_exchange_v4_dev_submissions s ON s.order_id=x.order_id AND s.journal_epoch=x.journal_epoch AND s.owner_user_id=x.owner_user_id
            WHERE x.command_id=@id AND x.journal_epoch=@epoch AND x.decision_sequence=@seq AND x.rejected=false AND s.phase=2 AND s.decision_sequence<@seq
            """ : """
            SELECT payload FROM qd_exchange_v4_dev_submissions
            WHERE command_id=@id AND journal_epoch=@epoch AND phase=2 AND decision_sequence=@seq
            """;
        await using var q = new NpgsqlCommand(sql,c,tx);
        q.Parameters.AddWithValue("id", ExchangeWire.ParseId(record.CommandId));
        q.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
        q.Parameters.AddWithValue("seq",record.CommandSequence);
        if (await q.ExecuteScalarAsync(ct) is not byte[] bytes)
            throw new InvalidOperationException("Delivery has no durably accepted authorization.");
        var order = EquityLimitOrder.Parser.ParseFrom(bytes);
        ExchangeWire.ValidateOrder(order);
        if (cancellation)
        {
            if (!record.Cancelled.Book.Equals(order.Book) || record.Cancelled.AccountId != order.AccountId || record.Cancelled.OrderId != order.OrderId)
                throw new InvalidDataException("Cancellation authorization payload mismatch.");
        }
        else if (!record.Submitted.Order.Equals(order))
            throw new InvalidDataException("Submission authorization payload mismatch.");
    }

    // Release only a durably authorized, completely unfilled rejected submission.
    // The ordered cursor and immutable decision make a crash after M07 commit safe:
    // M07's release is idempotent and the same decision will be replayed.
    private async Task ApplyRejectionAsync(NpgsqlConnection c, NpgsqlTransaction tx, V.RejectedDecision record, CancellationToken ct)
    {
        if (record.RejectedRegistration is not null) return; // trusted book provisioning, no financial effect
        var cancellation = record.RejectedCancel is not null;
        var sql = cancellation ? """
            SELECT s.payload,s.owner_user_id FROM qd_exchange_v4_dev_cancellations x
            JOIN qd_exchange_v4_dev_submissions s ON s.order_id=x.order_id AND s.journal_epoch=x.journal_epoch AND s.owner_user_id=x.owner_user_id
            WHERE x.command_id=@id AND x.journal_epoch=@epoch AND x.decision_sequence=@seq AND x.rejected=true AND s.phase=2 AND s.decision_sequence<@seq
            """ : """
            SELECT payload,owner_user_id FROM qd_exchange_v4_dev_submissions
            WHERE command_id=@id AND journal_epoch=@epoch AND phase=3 AND decision_sequence=@seq
            """;
        EquityLimitOrder order;
        Guid owner;
        await using (var q = new NpgsqlCommand(sql,c,tx))
        {
            q.Parameters.AddWithValue("id", ExchangeWire.ParseId(record.CommandId));
            q.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
            q.Parameters.AddWithValue("seq", record.CommandSequence);
            await using var reader = await q.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Rejection has no durably recorded authorization.");
            order = EquityLimitOrder.Parser.ParseFrom(reader.GetFieldValue<byte[]>(0));
            owner = reader.GetGuid(1);
        }
        ExchangeWire.ValidateOrder(order);
        if (cancellation)
        {
            if (record.RejectedCancel is not { } rejectedCancel || !rejectedCancel.Book.Equals(order.Book) || rejectedCancel.OrderId != order.OrderId || rejectedCancel.AccountId != order.AccountId)
                throw new InvalidDataException("Rejected cancellation payload mismatch.");
            return; // rejected cancel is NEVER release authorization
        }
        if (!order.Equals(record.RejectedSubmit)) throw new InvalidDataException("Rejected submission payload mismatch.");
        var hold = await ReadReservationAsync(order.OrderId, order.AccountId, order.Book, ct);
        if (hold.Side != (order.Side == Side.Buy ? EquityReservationSide.Buy : EquityReservationSide.Sell) ||
            hold.OriginalQuantity != order.Quantity || hold.LimitPrice != ExchangeWire.ToUsd(order.LimitPriceUnits) ||
            hold.FilledQuantity != 0 || hold.Status is not (EquityReservationStatus.Active or EquityReservationStatus.Released))
            throw new InvalidOperationException("Rejected submission does not match its unfilled reservation.");
        await _reservations.ReleaseConfirmedAsync(owner, hold.AccountId, hold.SessionId, hold.OrderId, ct);
    }

    private async Task ApplyAsync(DeliveryRecord record, CancellationToken ct)
    {
        switch (record.EventCase)
        {
            case DeliveryRecord.EventOneofCase.Registered:
                // Registration has no financial effect. Session authorization is upstream.
                return;
            case DeliveryRecord.EventOneofCase.Submitted:
                var order = record.Submitted.Order;
                var reservation = await ReadReservationAsync(order.OrderId, order.AccountId, order.Book, ct);
                if (reservation.Side != (order.Side == Side.Buy ? EquityReservationSide.Buy : EquityReservationSide.Sell) ||
                    reservation.OriginalQuantity != order.Quantity || reservation.LimitPrice != ExchangeWire.ToUsd(order.LimitPriceUnits) ||
                    reservation.Status == EquityReservationStatus.Released)
                    throw new InvalidOperationException("Submission does not match its reserved order.");
                // A previously applied fill can already have made the reservation
                // Filled. Let M07 compare the persisted execution receipt on retry.
                foreach (var wire in record.Submitted.Fills)
                {
                    var fill = ExchangeWire.MapFill(wire);
                    await _settlement.SettleAsync(new TrustedReservedEquityExecution(fill.ExecutionId,
                        fill.BuyerAccountId, fill.SellerAccountId, fill.InstrumentId, fill.Quantity,
                        fill.ExecutionPrice, fill.BuyOrderId, fill.SellOrderId, fill.SessionId), ct);
                }
                return;
            case DeliveryRecord.EventOneofCase.Cancelled:
                var cancel = record.Cancelled;
                var cancelledReservation = await ReadReservationAsync(cancel.OrderId, cancel.AccountId, cancel.Book, ct);
                await using (var db = new QuantDeskDbContext(_options))
                {
                    var owner = await db.ComptesTrading.AsNoTracking().Where(a => a.Id == cancelledReservation.AccountId)
                        .Select(a => a.OwnerUserId).SingleAsync(ct);
                    await _reservations.ReleaseConfirmedAsync(owner, cancelledReservation.AccountId,
                        cancelledReservation.SessionId, cancelledReservation.OrderId, ct);
                }
                return;
            default: throw new InvalidDataException("Unsupported delivery event.");
        }
    }

    private async Task<EquityOrderReservation> ReadReservationAsync(string order, string account, BookKey book, CancellationToken ct)
    {
        var orderId = ExchangeWire.ParseId(order);
        var accountId = ExchangeWire.ParseId(account);
        var sessionId = ExchangeWire.ParseId(book.SessionId);
        await using var db = new QuantDeskDbContext(_options);
        var result = await db.EquityOrderReservations.AsNoTracking().SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (result is null || result.AccountId != accountId || result.SessionId != sessionId ||
            !string.Equals(result.InstrumentId, book.InstrumentId, StringComparison.Ordinal))
            throw new InvalidOperationException("Delivery has no matching reservation.");
        return result;
    }
}

public sealed record DeliveryProgress(long LastCommandSequence, long HighWatermark, bool AppliedCommand);

