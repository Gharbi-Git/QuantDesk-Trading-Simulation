using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Exchange.Client;
using QuantDesk.Exchange.Outcomes.Client;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;
using W = QuantDesk.Contracts.V1.Exchange;
using V = QuantDesk.Contracts.V2.Exchange;

namespace QuantDesk.Exchange.Operational;

// Explicitly driven test-only composition. No HTTP endpoints, no runtime DDL,
// no implicit background tasks. All cooperating calls in this process are serialized;
// a database session advisory lock also excludes other cooperating processes.
// M07 commits independently. No external writes/releases or second engine
// are supported. Production admission, lock-loss fencing and lifecycle pending.
public sealed class OperationalTradingFlow
{
    private static readonly SemaphoreSlim Gate = new(1,1);
    private readonly OutcomeExchangeClient exchange;
    private readonly DbContextOptions<QuantDeskDbContext> options;
    private readonly string connectionString;
    private readonly PortfolioReservationService reservations;
    private readonly ReservedEquitySettlementService settlement;
    private Guid Epoch => exchange.JournalEpoch;
    public OperationalTradingFlow(OutcomeExchangeClient exchange, DbContextOptions<QuantDeskDbContext> options)
    {
        this.exchange = exchange; this.options = options;
        using var db = new QuantDeskDbContext(options);
        if (db.Database.GetDbConnection() is not NpgsqlConnection c || c.Database != "quantdesk_test")
            throw new InvalidOperationException("Operational flow candidate requires quantdesk_test.");
        connectionString = new NpgsqlConnectionStringBuilder(c.ConnectionString) { Pooling = false }.ConnectionString;
        reservations = new(options); settlement = new(options);
    }
    private async Task<T> Locked<T>(Func<NpgsqlConnection,Task<T>> action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var c = new NpgsqlConnection(connectionString); await c.OpenAsync(ct);
            await using var q = new NpgsqlCommand("SELECT pg_advisory_lock(6061203::bigint)",c);
            await q.ExecuteNonQueryAsync(ct);
            // Non-pooled connection disposal releases this session lock, even on error.
            return await action(c);
        }
        finally { Gate.Release(); }
    }
    private static async Task<T> Tx<T>(NpgsqlConnection c, Func<OperationalCommandStore,Task<T>> action,CancellationToken ct)
    {
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var result = await action(new(c,tx)); await tx.CommitAsync(ct); return result;
    }
    private async Task<JournalBinding> Observe(NpgsqlConnection c,CancellationToken ct)
    {
        var status = await exchange.GetStatusAsync(ct);
        return await Tx(c,async s => { await s.ObserveAsync(status,ct); return await s.GetBindingAsync(ct); },ct);
    }
    public Task BindEmptyAsync(CancellationToken ct=default) => Locked(async c => {
        var status=await exchange.GetStatusAsync(ct);
        return await Tx(c,async s=>{await s.BindEmptyAsync(status,ct);return true;},ct);
    },ct);
    public Task PrepareRegistrationAsync(Guid id,W.BookKey book,CancellationToken ct=default) => Locked(async c=>{
        await Observe(c,ct);
        return await Tx(c,async s=>{await s.PrepareRegistrationAsync(Epoch,id,book,ct);return true;},ct);
    },ct);
    public Task PrepareCancellationAsync(Guid owner,Guid id,Guid targetSubmission,CancellationToken ct=default) => Locked(async c=>{
        await Observe(c,ct);
        return await Tx(c,async s=>{await s.PrepareCancellationAsync(Epoch,id,owner,targetSubmission,ct);return true;},ct);
    },ct);
    // Admission strings are fixture references only. They are not a risk decision.
    public Task PrepareSubmissionAsync(Guid owner,Guid id,W.EquityLimitOrder request,string admission,string riskVersion,CancellationToken ct=default) => Locked(async c=>{
        await Observe(c,ct); var order=request.Clone(); ExchangeWire.ValidateOrder(order);
        var row=await Tx(c,async s=>{
            // Reject adoption of an unrelated existing reservation by a new command.
            await using var q=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM qd_exchange_v4.commands WHERE command_id=@id)",c);
            q.Parameters.AddWithValue("id",id);
            if (!(bool)(await q.ExecuteScalarAsync(ct))!)
            {
                await using var db=new QuantDeskDbContext(options);
                var oid=ExchangeWire.ParseId(order.OrderId);
                if(await db.EquityOrderReservations.AnyAsync(r=>r.OrderId==oid,ct)) throw new InvalidOperationException("Existing reservation outside operational intent.");
            }
            await s.PrepareSubmissionAsync(Epoch,id,owner,order,admission,riskVersion,ct);
            return await s.ReadAsync(id,ct);
        },ct);
        if(row.Phase=="pending")
        {
            var hold=await reservations.ReserveAsync(owner,ExchangeWire.ParseId(order.OrderId),ExchangeWire.ParseId(order.AccountId),
                ExchangeWire.ParseId(order.Book.SessionId),order.Book.InstrumentId,Side(order),order.Quantity,ExchangeWire.ToUsd(order.LimitPriceUnits),ct);
            if(hold.Status!=EquityReservationStatus.Active || hold.RemainingQuantity!=order.Quantity)
                throw new InvalidOperationException("Full active reservation required.");
            await Tx(c,async s=>{await s.MarkReservationConfirmedAsync(id,ct);return true;},ct);
        }
        return true;
    },ct);
    public Task<V.CommandReceipt> DispatchAsync(Guid id,CancellationToken ct=default) => Locked(c=>DispatchOnConnection(c,id,ct),ct);
    private async Task<V.CommandReceipt> DispatchOnConnection(NpgsqlConnection c,Guid id,CancellationToken ct)
    {
        await Observe(c,ct);
        var row=await Tx(c,s=>s.ReadAsync(id,ct),ct);
        if(row.Epoch!=Epoch || (row.Kind=="submit" && row.Phase=="pending")) throw new InvalidOperationException("Command not ready for dispatch.");
        var receipt=await exchange.GetCommandReceiptAsync(id,ct);
        if(receipt is null)
        {
            if(row.Phase is "accepted" or "rejected") throw new InvalidDataException("Recorded receipt missing from journal.");
            switch(row.Kind)
            {
                case "register": receipt=await exchange.RegisterBookAsync(id,W.RegisterBookRequest.Parser.ParseFrom(row.Payload).Book,ct);break;
                case "submit":
                    var order=W.SubmitOrderRequest.Parser.ParseFrom(row.Payload).Order;
                    var hold=await ReadHold(order.OrderId,order.AccountId,order.Book,ct);
                    RequireExactHold(hold,order);
                    if(hold.Status!=EquityReservationStatus.Active || hold.RemainingQuantity!=order.Quantity) throw new InvalidOperationException("Full active reservation required before RPC.");
                    receipt=await exchange.SubmitOrderAsync(id,order,ct);break;
                case "cancel":
                    var cancel=W.CancelOrderRequest.Parser.ParseFrom(row.Payload);
                    receipt=await exchange.CancelOrderAsync(id,cancel.Book,ExchangeWire.ParseId(cancel.OrderId),ExchangeWire.ParseId(cancel.AccountId),ct);break;
                default:throw new InvalidDataException("Unknown command kind.");
            }
        }
        var page=await exchange.GetDeliveryBatchAsync(OutcomeWire.Sequence(receipt.Record)-1,1,ct);
        await Observe(c,ct);
        await Tx(c,async s=>{await s.RecordDecisionAsync(id,receipt,page,ct);return true;},ct);
        return receipt;
    }
    public Task<OperationalProgress> ProcessNextAsync(CancellationToken ct=default) => ProcessCore(false,ct);
    // Explicit failure injection, test-only: M07 commits, cursor must stay put.
    public Task<OperationalProgress> TestInterruptAfterEffectsAsync(CancellationToken ct=default) => ProcessCore(true,ct);
    private Task<OperationalProgress> ProcessCore(bool interrupt,CancellationToken ct) => Locked(c=>ProcessOnConnection(c,interrupt,ct),ct);
    private async Task<OperationalProgress> ProcessOnConnection(NpgsqlConnection c,bool interrupt,CancellationToken ct)
    {
        var binding=await Observe(c,ct);
        var batch=await exchange.GetDeliveryBatchAsync(binding.Cursor,1,ct);
        OutcomeWire.ValidateDelivery(batch,Epoch,binding.Cursor,1);
        if(batch.Records.Count==0) return new OperationalProgress(binding.Cursor,batch.HighWatermark,false);
        var record=batch.Records.Single();var id=ExchangeWire.ParseId(OutcomeWire.CommandId(record));var seq=OutcomeWire.Sequence(record);
        // Unknown or unreconciled intents block before any effect.
        var row=await Tx(c,s=>s.ReadAsync(id,ct),ct);
        if(row.Epoch!=Epoch || row.Sequence!=seq || row.Decision is null || !V.DecisionRecord.Parser.ParseFrom(row.Decision).Equals(record))
            throw new InvalidOperationException("Delivery has no recorded operational decision.");
        await Apply(c,row,record,ct);
        if(interrupt) throw new InvalidOperationException("Test interruption after effects.");
        await Tx(c,async s=>{await s.AdvanceAfterEffectsAsync(Epoch,binding.Cursor,seq,ct);return true;},ct);
        return new OperationalProgress(seq,batch.HighWatermark,true);
    }
    // Recovery is serialized with the existing flow. Always drain committed journal
    // decisions before considering a new RPC. New dispatch is opt-in for fixtures only.
    public Task<bool> RecoverOneAsync(bool resumePreparedTestCommands=false,CancellationToken ct=default) => Locked(async c=>{
        var binding=await Observe(c,ct);
        var page=await exchange.GetDeliveryBatchAsync(binding.Cursor,1,ct);
        OutcomeWire.ValidateDelivery(page,Epoch,binding.Cursor,1);
        if(page.Records.Count!=0)
        {
            var record=page.Records.Single();
            var id=ExchangeWire.ParseId(OutcomeWire.CommandId(record));
            // Check local authorization BEFORE attempting reconciliation; unknown rows fail closed.
            var row=await Tx(c,s=>s.ReadAsync(id,ct),ct);
            if(row.Epoch!=Epoch || (row.Kind=="submit" && row.Phase=="pending"))
                throw new InvalidOperationException("Recovery requires authorized, reservation-confirmed intent.");
            var receipt=await exchange.GetCommandReceiptAsync(id,ct)
                ?? throw new InvalidDataException("Journal delivery has no durable receipt.");
            await Observe(c,ct);
            await Tx(c,async s=>{await s.RecordDecisionAsync(id,receipt,page,ct);return true;},ct);
            var result=await ProcessOnConnection(c,false,ct);
            if(!result.AppliedCommand) throw new InvalidDataException("Recovery did not advance committed delivery.");
            return true;
        }
        if(!resumePreparedTestCommands) return false;
        // Never manufacture a reservation or re-admit an unconfirmed submission.
        // These test-only intents carry fixture admission; production revalidation is not implemented.
        Guid? ready;
        await using(var q=new NpgsqlCommand("""
            SELECT command_id FROM qd_exchange_v4.commands WHERE journal_epoch=@epoch
            AND ((kind='submit' AND phase='reserved') OR (kind IN ('register','cancel') AND phase='pending'))
            ORDER BY created_at,command_id LIMIT 1
            """,c))
        {
            q.Parameters.AddWithValue("epoch",Epoch);
            ready=(await q.ExecuteScalarAsync(ct)) is Guid id ? id : null;
        }
        if(ready is null) return false;
        await DispatchOnConnection(c,ready.Value,ct);
        return true;
    },ct);
    private async Task Apply(NpgsqlConnection c,StoredCommand row,V.DecisionRecord record,CancellationToken ct)
    {
        if(row.Kind=="register") return;
        if(row.Kind=="cancel")
        {
            if(record.Rejected is not null) return;
            var cancel=record.Accepted.Cancelled;
            var hold=await ReadHold(cancel.OrderId,cancel.AccountId,cancel.Book,ct);
            await reservations.ReleaseConfirmedAsync(row.Owner!.Value,hold.AccountId,hold.SessionId,hold.OrderId,ct);
            return;
        }
        var order=W.SubmitOrderRequest.Parser.ParseFrom(row.Payload).Order;
        var reservation=await ReadHold(order.OrderId,order.AccountId,order.Book,ct);RequireExactHold(reservation,order);
        if(record.Rejected is not null)
        {
            if(reservation.FilledQuantity!=0 || reservation.Status is not (EquityReservationStatus.Active or EquityReservationStatus.Released))
                throw new InvalidOperationException("Rejected submission must be unfilled.");
            await reservations.ReleaseConfirmedAsync(row.Owner!.Value,reservation.AccountId,reservation.SessionId,reservation.OrderId,ct);return;
        }
        if(reservation.Status==EquityReservationStatus.Released) throw new InvalidOperationException("Accepted submission already released.");
        foreach(var wire in record.Accepted.Submitted.Fills)
        {
            var fill=ExchangeWire.MapFill(wire);
            // Both orders must have durable accepted decisions in this epoch.
            foreach(var orderId in new[]{fill.BuyOrderId,fill.SellOrderId})
            {
                await using var q=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM qd_exchange_v4.commands WHERE journal_epoch=@epoch AND kind='submit' AND order_id=@order AND phase='accepted' AND decision_sequence<=@seq)",c);
                q.Parameters.AddWithValue("epoch",Epoch);q.Parameters.AddWithValue("order",orderId);q.Parameters.AddWithValue("seq",row.Sequence!.Value);
                if(!(bool)(await q.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Fill lacks accepted counterparty intent.");
            }
        }
        foreach(var wire in record.Accepted.Submitted.Fills)
        {
            var fill=ExchangeWire.MapFill(wire);
            await settlement.SettleAsync(new TrustedReservedEquityExecution(fill.ExecutionId,fill.BuyerAccountId,fill.SellerAccountId,fill.InstrumentId,fill.Quantity,fill.ExecutionPrice,fill.BuyOrderId,fill.SellOrderId,fill.SessionId),ct);
        }
    }
    private async Task<EquityOrderReservation> ReadHold(string order,string account,W.BookKey book,CancellationToken ct)
    {
        var id=ExchangeWire.ParseId(order);
        await using var db=new QuantDeskDbContext(options);
        var hold=await db.EquityOrderReservations.AsNoTracking().SingleOrDefaultAsync(r=>r.OrderId==id,ct);
        if(hold is null || hold.AccountId!=ExchangeWire.ParseId(account) || hold.SessionId!=ExchangeWire.ParseId(book.SessionId) || hold.InstrumentId!=book.InstrumentId)
            throw new InvalidOperationException("No matching reservation.");
        return hold;
    }
    private static EquityReservationSide Side(W.EquityLimitOrder o)=>o.Side==W.Side.Buy?EquityReservationSide.Buy:EquityReservationSide.Sell;
    private static void RequireExactHold(EquityOrderReservation h,W.EquityLimitOrder o)
    {
        if(h.Side!=Side(o)||h.OriginalQuantity!=o.Quantity||h.LimitPrice!=ExchangeWire.ToUsd(o.LimitPriceUnits)) throw new InvalidOperationException("Reservation payload mismatch.");
    }
}
public sealed record OperationalProgress(long LastCommandSequence,long HighWatermark,bool AppliedCommand);
