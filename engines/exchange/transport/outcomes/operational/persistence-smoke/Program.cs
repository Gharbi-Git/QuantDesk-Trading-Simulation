using Google.Protobuf;
using Npgsql;
using QuantDesk.Exchange.Client;
using QuantDesk.Exchange.Operational;
using W = QuantDesk.Contracts.V1.Exchange;
using V = QuantDesk.Contracts.V2.Exchange;

// Deliberately synthetic decisions and reservation/effects markers: storage-only
// integration with real PostgreSQL, not a trading or M07 test.
if (args.Length != 1) throw new ArgumentException("Expected schema migration path.");
var raw = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("Test connection required.");
if (new NpgsqlConnectionStringBuilder(raw).Database != "quantdesk_test") throw new InvalidOperationException("Test database required.");
await using var c = new NpgsqlConnection(raw); await c.OpenAsync();
if (c.Database != "quantdesk_test") throw new InvalidOperationException("Test database required.");
await using (var tx = await c.BeginTransactionAsync())
{
    async Task Exec(string sql) { await using var q = new NpgsqlCommand(sql,c,tx); await q.ExecuteNonQueryAsync(); }
    await Exec("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'; DO $$ BEGIN IF current_database() <> 'quantdesk_test' OR to_regnamespace('qd_exchange_v4') IS NOT NULL THEN RAISE EXCEPTION 'Test database with no operational schema required'; END IF; END $$;");
    await Exec(await File.ReadAllTextAsync(args[0]));
    var store = new OperationalCommandStore(c,tx);
    var epoch=Guid.NewGuid(); var owner=Guid.NewGuid(); var account=Guid.NewGuid(); var session=Guid.NewGuid();
    const string instrument="EQ-OPERATIONAL-TEST";
    var book=new W.BookKey {SessionId=ExchangeWire.Id(session), InstrumentId=instrument};
    var reg=Guid.NewGuid(); var sub=Guid.NewGuid(); var cancel=Guid.NewGuid(); var rejected=Guid.NewGuid();
    var order=ExchangeWire.CreateOrder(session,instrument,Guid.NewGuid(),account,W.Side.Buy,150m,40);
    var other=ExchangeWire.CreateOrder(session,instrument,Guid.NewGuid(),account,W.Side.Buy,149m,10);
    V.StatusReply Status(long head) => new(){JournalEpoch=ExchangeWire.Id(epoch),JournalFormat=4,Healthy=true,HighWatermark=head};
    V.CommandReceipt Receipt(V.DecisionRecord record)=>new(){JournalEpoch=ExchangeWire.Id(epoch),Record=record};
    V.DeliveryBatch Page(V.CommandReceipt receipt,long head){var p=new V.DeliveryBatch{JournalEpoch=receipt.JournalEpoch,HighWatermark=head};p.Records.Add(receipt.Record.Clone());return p;}
    W.DeliveryRecord Accepted(Guid id,long seq)=>new(){CommandId=ExchangeWire.Id(id),CommandSequence=seq};
    V.RejectedDecision Rejected(Guid id,long seq)=>new(){CommandId=ExchangeWire.Id(id),CommandSequence=seq,Reason=V.RejectionReason.DomainRejected};
    await store.BindEmptyAsync(Status(0)); Check(await store.ObserveAsync(Status(0))==0,"initial cursor");
    await store.PrepareRegistrationAsync(epoch,reg,book); await store.PrepareRegistrationAsync(epoch,reg,book);
    await Reject<InvalidOperationException>(()=>store.PrepareRegistrationAsync(epoch,reg,new W.BookKey{SessionId=book.SessionId,InstrumentId="OTHER"}));
    await store.PrepareSubmissionAsync(epoch,sub,owner,order,"fixture-admission","fixture-risk-v1");
    await Reject<InvalidOperationException>(()=>store.PrepareSubmissionAsync(epoch,sub,owner,order,"different","fixture-risk-v1"));
    Check((await store.PendingAsync(epoch)).Count==2,"pending intents");
    var a1=Accepted(reg,1);a1.Registered=new W.RegisteredBook{Book=book.Clone()};var r1=Receipt(new(){Accepted=a1});
    var a2=Accepted(sub,2);a2.Submitted=new W.SubmittedOrder{Order=order.Clone(),State=W.OrderState.Accepted,OpenQuantity=order.Quantity};var r2=Receipt(new(){Accepted=a2});
    Check(await store.ObserveAsync(Status(2))==0,"observe head");
    await store.RecordDecisionAsync(reg,r1,Page(r1,2));
    await Reject<InvalidOperationException>(()=>store.RecordDecisionAsync(sub,r2,Page(r2,2)));
    await store.MarkReservationConfirmedAsync(sub); await store.MarkReservationConfirmedAsync(sub);
    var changed=r2.Clone();changed.Record.Accepted.Submitted.Order.LimitPriceUnits++;
    await Reject<InvalidDataException>(()=>store.RecordDecisionAsync(sub,changed,Page(changed,2)));
    await store.RecordDecisionAsync(sub,r2,Page(r2,2));
    await store.RecordDecisionAsync(sub,r2,Page(r2,2));
    Check((await store.ReadAsync(sub)).Decision!.SequenceEqual(r2.Record.ToByteArray()),"decision bytes round-trip");
    Check(W.SubmitOrderRequest.Parser.ParseFrom((await store.ReadAsync(sub)).Payload).Order.Equals(order),"request bytes round-trip");
    Console.WriteLine("PASS: typed intents, exact retry, reservation marker, receipt/delivery and payload verification");
    await Reject<InvalidOperationException>(()=>store.PrepareCancellationAsync(epoch,cancel,Guid.NewGuid(),sub));
    await store.PrepareCancellationAsync(epoch,cancel,owner,sub);
    await store.PrepareSubmissionAsync(epoch,rejected,owner,other,"fixture-admission-2","fixture-risk-v1"); await store.MarkReservationConfirmedAsync(rejected);
    var a3=Rejected(cancel,3);a3.RejectedCancel=new W.CancelledOrder{Book=book.Clone(),OrderId=order.OrderId,AccountId=order.AccountId};var r3=Receipt(new(){Rejected=a3});
    var a4=Rejected(rejected,4);a4.RejectedSubmit=other.Clone();var r4=Receipt(new(){Rejected=a4});
    await store.ObserveAsync(Status(4)); await store.RecordDecisionAsync(cancel,r3,Page(r3,4));await store.RecordDecisionAsync(rejected,r4,Page(r4,4));
    Check((await store.ReadAsync(cancel)).Phase=="rejected" && (await store.ReadAsync(rejected)).Phase=="rejected","rejected decisions");
    Check((await store.PendingAsync(epoch)).Count==0,"no pending commands");
    Console.WriteLine("PASS: owned cancellation, accepted/rejected decisions and recovery queries");
    // Simulate successful effects in this storage-only test; no actual financial data touched.
    for(long i=0;i<4;i++) await store.AdvanceAfterEffectsAsync(epoch,i,i+1);
    var reopened=new OperationalCommandStore(c,tx);
    Check((await reopened.GetBindingAsync())==new JournalBinding(epoch,4,4),"binding and cursor read-back");
    await reopened.RecordDecisionAsync(sub,r2,Page(r2,4));
    await Exec("SAVEPOINT stale_cursor");
    try { await reopened.AdvanceAfterEffectsAsync(epoch,3,4); throw new Exception("Expected stale cursor failure"); }
    catch(PostgresException e) when(e.SqlState=="23514"){await Exec("ROLLBACK TO SAVEPOINT stale_cursor");}
    Console.WriteLine("PASS: monotonic cursor, stale checkpoint blocked, settled decision replay");
    await tx.RollbackAsync();
}
await using(var q=new NpgsqlCommand("SELECT to_regnamespace('qd_exchange_v4') IS NULL",c)) Check((bool)(await q.ExecuteScalarAsync())!,"schema rolled back");
Console.WriteLine("M06 OPERATIONAL PERSISTENCE SMOKE PASSED - ROLLED BACK");
static void Check(bool value,string message){if(!value)throw new InvalidOperationException("FAIL: "+message);}
static async Task Reject<T>(Func<Task> work) where T:Exception {try{await work();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
