using System.Diagnostics;
using System.Text.RegularExpressions;
using Grpc.Core;
using QuantDesk.Exchange.Client;
using QuantDesk.Exchange.Outcomes.Client;
using W = QuantDesk.Contracts.V1.Exchange;
using V = QuantDesk.Contracts.V2.Exchange;

if (args.Length != 1) throw new ArgumentException("Expected native V2 executable");
var epoch = Guid.NewGuid();
var dir = Directory.CreateTempSubdirectory("qd-outcomes-");
var journal = Path.Combine(dir.FullName,"journal");
var book = new W.BookKey { SessionId = Guid.NewGuid().ToString("D"), InstrumentId = "TEST" };
var owner = Guid.NewGuid(); var other = Guid.NewGuid();
var bad = ExchangeWire.CreateOrder(Guid.Parse(book.SessionId), book.InstrumentId, Guid.NewGuid(), owner, W.Side.Buy, 150m, 10);
var rejectId = Guid.NewGuid(); var cancelId = Guid.NewGuid();
V.CommandReceipt first = null!, fill = null!, cancel = null!;
try
{
    await using (var server = await DevServer.Start(args[0], "--create-dev-journal", journal, epoch))
    {
        var c = server.Client;
        Check((await c.GetStatusAsync()).JournalFormat == 4, "V4 negotiation");
        // Lost reply simulation: discard the actual network result, then look it up.
        _ = await c.SubmitOrderAsync(rejectId,bad);
        first = (await c.GetCommandReceiptAsync(rejectId))!;
        Check(first.Record.Rejected?.RejectedSubmit.Equals(bad) == true && first.Record.Rejected.Reason == V.RejectionReason.DomainRejected && OutcomeWire.Sequence(first.Record) == 1, "durable rejected payload");
        Check(await c.GetCommandReceiptAsync(Guid.NewGuid()) is null, "missing receipt unknown");
        Check((await c.RegisterBookAsync(Guid.NewGuid(),book)).Record.Accepted?.Registered is not null,"register");
        var duplicateRegistration = await c.RegisterBookAsync(Guid.NewGuid(),book);
        Check(duplicateRegistration.Record.Accepted?.Registered?.Book.Equals(book)==true,"registration remains idempotent by book");
        Check((await c.SubmitOrderAsync(rejectId,bad)).Equals(first),"retry after book registered stays rejected");
        var changed=bad.Clone();changed.Quantity++;
        await MustFail(() => c.SubmitOrderAsync(rejectId,changed),StatusCode.AlreadyExists);
        var sell=ExchangeWire.CreateOrder(Guid.Parse(book.SessionId),book.InstrumentId,Guid.NewGuid(),other,W.Side.Sell,149m,20);
        Check((await c.SubmitOrderAsync(Guid.NewGuid(),sell)).Record.Accepted?.Submitted is not null,"sell accepted");
        var buy=bad.Clone();buy.OrderId=Guid.NewGuid().ToString("D");
        fill=await c.SubmitOrderAsync(Guid.NewGuid(),buy);
        var trade=fill.Record.Accepted.Submitted.Fills.Single();
        Check(trade.Quantity==10 && trade.ExecutionPriceUnits==1490000 && trade.ExecutionTimeUtcUnixMicros>0,"real matching");
        cancel=await c.CancelOrderAsync(cancelId,book,Guid.Parse(sell.OrderId),owner);
        Check(cancel.Record.Rejected?.RejectedCancel?.AccountId==owner.ToString("D"),"rejected cancel retains owner");
        Check((await c.CancelOrderAsync(Guid.NewGuid(),book,Guid.Parse(sell.OrderId),other)).Record.Accepted?.Cancelled is not null,"accepted cancel");
        var page=await c.GetDeliveryBatchAsync(0,64);
        Check(page.HighWatermark==7&&page.Records.Count==7&&page.Records[0].Equals(first.Record)&&page.Records[4].Equals(fill.Record)&&page.Records[5].Equals(cancel.Record),"contiguous mixed delivery");
        // Native numeric domain rejection (the individual requests are valid, but depth would overflow).
        var depthBook=book.Clone();depthBook.InstrumentId="DEPTH-OVERFLOW";
        await c.RegisterBookAsync(Guid.NewGuid(),depthBook);
        var huge=ExchangeWire.CreateOrder(Guid.Parse(depthBook.SessionId),depthBook.InstrumentId,Guid.NewGuid(),owner,W.Side.Buy,0.0001m,long.MaxValue);
        Check((await c.SubmitOrderAsync(Guid.NewGuid(),huge)).Record.Accepted?.Submitted is not null,"valid huge order");
        var extra=huge.Clone();extra.OrderId=Guid.NewGuid().ToString("D");extra.Quantity=1;
        var numeric=await c.SubmitOrderAsync(Guid.NewGuid(),extra);
        Check(numeric.Record.Rejected?.Reason==V.RejectionReason.NumericRejected && numeric.Record.Rejected.RejectedSubmit.Equals(extra),"numeric rejection reason and payload");
        Check((await c.GetDeliveryBatchAsync(7,64)).Records[2].Equals(numeric.Record),"numeric rejection delivered");
        var tampered=page.Clone();tampered.Records[0].Rejected.Reason=V.RejectionReason.Unspecified;
        MustReject(() => OutcomeWire.ValidateDelivery(tampered,epoch,0,64));
        tampered=page.Clone();tampered.Records.RemoveAt(0);
        MustReject(() => OutcomeWire.ValidateDelivery(tampered,epoch,0,64));
        MustReject(() => OutcomeWire.RequireSubmission(first.Record,changed));
        using var wrong=new OutcomeExchangeClient(server.Port,Guid.NewGuid());
        await MustFail(() => wrong.GetDeliveryBatchAsync(0),StatusCode.FailedPrecondition);
        using var legacy=new GrpcExchangeClient(server.Port,epoch);
        await MustFail(() => legacy.GetStatusAsync(),StatusCode.Unimplemented);
        Console.WriteLine("PASS: all six V2 RPCs, accepted/rejected payloads, stable retry, exact matching, mixed delivery and V1/epoch fencing");
    }
    await using (var server=await DevServer.Start(args[0],"--open-dev-journal",journal,epoch))
    {
        var c=server.Client;
        Check((await c.GetCommandReceiptAsync(rejectId))!.Equals(first),"rejected receipt after restart");
        Check((await c.GetCommandReceiptAsync(cancelId))!.Equals(cancel),"rejected cancel after restart");
        Check((await c.GetDeliveryBatchAsync(4,1)).Records.Single().Equals(fill.Record),"stable fill after restart");
        Check((await c.SubmitOrderAsync(rejectId,bad)).Equals(first),"stable retry after restart");
        Check((await c.GetStatusAsync()).HighWatermark==10,"no new command after retry");
        Console.WriteLine("PASS: restart preserves rejection, original payload and execution ID/time");
    }
    Console.WriteLine("V2 OUTCOME CROSS-LANGUAGE SMOKE PASSED");
}
finally { dir.Delete(recursive:true); }
static void Check(bool b,string s) { if(!b) throw new InvalidOperationException("FAIL: "+s); }
static async Task MustFail(Func<Task> call,StatusCode status)
{
    try { await call(); } catch(RpcException e) when(e.StatusCode==status) { return; }
    throw new InvalidOperationException("Expected RPC error "+status);
}
static void MustReject(Action call)
{
    try { call(); } catch(InvalidDataException) { return; }
    throw new InvalidOperationException("Expected wire validation failure");
}
sealed class DevServer : IAsyncDisposable
{
    private readonly Process process;
    public OutcomeExchangeClient Client {get;}
    public int Port {get;}
    private DevServer(Process p,int port,Guid epoch){process=p;Port=port;Client=new(port,epoch);}
    public static async Task<DevServer> Start(string exe,string mode,string file,Guid epoch)
    {
        var s=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true};
        s.ArgumentList.Add(mode);s.ArgumentList.Add(file);s.ArgumentList.Add(epoch.ToString("D"));
        var p=Process.Start(s)??throw new InvalidOperationException("Native server failed to start");
        try {
            var line=await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var match=Regex.Match(line??"",@"^DEV V2 ONLY: unencrypted exchange at 127\.0\.0\.1:(\d+); journal V4$");
            if(!match.Success||!int.TryParse(match.Groups[1].Value,out var port))throw new InvalidOperationException("Unexpected server startup: "+line);
            return new(p,port,epoch);
        } catch {await Stop(p);throw;}
    }
    public async ValueTask DisposeAsync(){Client.Dispose();await Stop(process);}
    private static async Task Stop(Process p){try{if(!p.HasExited)p.Kill(entireProcessTree:true);await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}finally{p.Dispose();}}
}
