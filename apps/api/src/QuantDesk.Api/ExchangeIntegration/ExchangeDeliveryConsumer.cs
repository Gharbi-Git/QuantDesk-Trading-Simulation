using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Contracts.V1.Exchange;
using QuantDesk.Exchange.Client;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;

namespace QuantDesk.Api.ExchangeIntegration;

/// <summary>Explicitly invoked development consumer. Test DB and isolated V3 epoch only.
/// One command at a time under a PostgreSQL checkpoint row lock. M07 commits each
/// effect separately; replays use its durable receipts and idempotent releases.
/// Do not expose via HTTP or run against live accounts.</summary>
public sealed class ExchangeDeliveryConsumer
{
    private readonly IExchangeClient _exchange;
    private readonly DbContextOptions<QuantDeskDbContext> _options;
    private readonly ReservedEquitySettlementService _settlement;
    private readonly PortfolioReservationService _reservations;

    public ExchangeDeliveryConsumer(IExchangeClient exchange, DbContextOptions<QuantDeskDbContext> options)
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
        await using var read = new NpgsqlCommand("SELECT last_command_sequence FROM qd_exchange_dev_checkpoints WHERE journal_epoch = @epoch FOR UPDATE", connection, tx);
        read.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
        if (await read.ExecuteScalarAsync(ct) is not long cursor || cursor < 0)
            throw new InvalidOperationException("No explicitly provisioned checkpoint for this epoch.");

        // Read one complete command, validate the entire message before any effect.
        var batch = await _exchange.GetDeliveryBatchAsync(cursor, 1, ct);
        ExchangeWire.ValidateDelivery(batch, _exchange.JournalEpoch, cursor, 1);
        if (batch.Records.Count == 0)
        {
            await tx.CommitAsync(ct);
            return new(cursor, batch.HighWatermark, false);
        }
        var record = batch.Records[0];
        await ApplyAsync(record, ct);
        // This is deliberately a separate transaction from M07 effects. If it
        // fails after a fill commits, retry that command; never skip its remainder.
        await using var update = new NpgsqlCommand("UPDATE qd_exchange_dev_checkpoints SET last_command_sequence = @next WHERE journal_epoch = @epoch AND last_command_sequence = @previous", connection, tx);
        update.Parameters.AddWithValue("next", record.CommandSequence);
        update.Parameters.AddWithValue("epoch", _exchange.JournalEpoch);
        update.Parameters.AddWithValue("previous", cursor);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Checkpoint changed unexpectedly.");
        await tx.CommitAsync(ct);
        return new(record.CommandSequence, batch.HighWatermark, true);
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
