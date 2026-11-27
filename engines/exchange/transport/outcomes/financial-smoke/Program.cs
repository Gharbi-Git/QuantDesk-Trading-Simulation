using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Exchange.Outcomes.Settlement;
using QuantDesk.Exchange.Outcomes.Client;
using QuantDesk.Contracts.V1.Exchange;
using QuantDesk.Exchange.Client;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;

if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Pass the native exchange executable.");
var raw = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("QD_TEST_CONNECTION_STRING required.");
if (new NpgsqlConnectionStringBuilder(raw).Database != "quantdesk_test") throw new InvalidOperationException("Use quantdesk_test only.");
var options = new DbContextOptionsBuilder<QuantDeskDbContext>().UseNpgsql(raw).Options;
var epoch = Guid.NewGuid();
var directory = Path.Combine(Path.GetTempPath(), "qd-settlement-" + epoch.ToString("N"));
var journal = Path.Combine(directory, "journal");
var buyer = CompteTrading.OpenPersonal(Guid.NewGuid(), 100000m);
var seller = CompteTrading.OpenPersonal(Guid.NewGuid(), 100000m);
var session = Guid.NewGuid();
var instrument = "EQ-" + Guid.NewGuid().ToString("N");
var book = new BookKey { SessionId = session.ToString("D"), InstrumentId = instrument };
var sellOne = Guid.NewGuid(); var sellTwo = Guid.NewGuid(); var buy = Guid.NewGuid();
var sellOneCommand = Guid.NewGuid(); var sellTwoCommand = Guid.NewGuid(); var buyCommand = Guid.NewGuid();
var dispatchTableReady = false;
var cancelTableReady = false;
var cancelCommand = Guid.NewGuid();
var rejectBuy = Guid.NewGuid(); var rejectSell = Guid.NewGuid();
var rejectBuyCommand = Guid.NewGuid(); var rejectSellCommand = Guid.NewGuid();
var pending = Guid.NewGuid(); var pendingCommand = Guid.NewGuid();
long releaseVersion = -1;
var provisioned = false;
var checkpointTableReady = false;
Directory.CreateDirectory(directory);
try
{
    await using (var db = new QuantDeskDbContext(options))
    {
        await db.Database.MigrateAsync();
        // Deliberately test-only schema. No changes to the shared EF model/snapshot.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS qd_exchange_v4_dev_checkpoints (journal_epoch uuid PRIMARY KEY, last_command_sequence bigint NOT NULL CHECK (last_command_sequence >= 0))");
        checkpointTableReady = true;
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var account in new[] { buyer, seller })
        {
            var tag = account.OwnerUserId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO utilisateurs (id, keycloak_subject, username, email, created_at_utc) VALUES ({account.OwnerUserId}, {"settlement-" + tag}, {"settle_" + tag}, {"test@example.invalid"}, {DateTime.UtcNow})");
            db.ComptesTrading.Add(account);
        }
        await db.SaveChangesAsync();
        // Synthetic inventory fixture only, not a production asset issuance path.
        db.PositionsAction.Add(PositionAction.OpenWithBuy(seller.Id, instrument, 100, 100m));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        provisioned = true;
    }
    await using (var server = await DevServer.Start(Path.GetFullPath(args[0]), "--create-dev-journal", journal, epoch))
    {
        var client = server.Client;
        Check((await client.GetStatusAsync()).HighWatermark == 0, "fresh V4 epoch");
        await using (var db = new QuantDeskDbContext(options))
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO qd_exchange_v4_dev_checkpoints (journal_epoch, last_command_sequence) VALUES ({epoch}, 0)");
        var dispatcher = new OutcomeOrderDispatcher(client, options);
        await dispatcher.ProvisionTestTableAsync(); dispatchTableReady = true;
        var canceller = new OutcomeCancellationDispatcher(client, options);
        await canceller.ProvisionTestTableAsync(); cancelTableReady = true;
        var consumer = new OutcomeDeliveryConsumer(client, options);
        var badBuy = ExchangeWire.CreateOrder(session,instrument,rejectBuy,buyer.Id,Side.Buy,150m,40);
        var badSell = ExchangeWire.CreateOrder(session,instrument,rejectSell,seller.Id,Side.Sell,149m,30);
        await dispatcher.PrepareAsync(buyer.OwnerUserId,rejectBuyCommand,badBuy);
        await dispatcher.PrepareAsync(seller.OwnerUserId,rejectSellCommand,badSell);
        Check((await Hold(rejectBuy)).ReservedCash == 6000m && (await Hold(rejectSell)).ReservedShares == 30,"holds before dispatch");
        Check(await client.GetCommandReceiptAsync(rejectBuyCommand) is null,"unknown decision");
        Check(!(await consumer.ProcessNextAsync()).AppliedCommand && (await Hold(rejectBuy)).ReservedCash == 6000m,"unknown does not release");
        // Deliberately drop the reply to a real submission of the exact prepared intent.
        _ = await client.SubmitOrderAsync(rejectBuyCommand,badBuy);
        await MustReject(() => consumer.ProcessNextAsync(),"Rejection has no durably recorded authorization.");
        Check((await Hold(rejectBuy)).ReservedCash == 6000m,"unreconciled rejection preserves hold");
        var decisions = await Task.WhenAll(dispatcher.DispatchAsync(rejectBuyCommand),new OutcomeOrderDispatcher(client,options).DispatchAsync(rejectBuyCommand));
        Check(decisions.All(x => x.Record.Rejected?.RejectedSubmit.Equals(badBuy)==true && OutcomeWire.Sequence(x.Record)==1),"lost rejection recovery and concurrent duplicate");
        Check((await Hold(rejectBuy)).ReservedCash == 6000m,"dispatcher does not release directly");
        Check((await consumer.ProcessNextAsync()).LastCommandSequence == 1,"consume rejection");
        var released = await Hold(rejectBuy); releaseVersion = released.Version;
        Check(released.Status == EquityReservationStatus.Released && released.ReservedCash == 0 && released.FilledQuantity==0,"cash released");
        // Rewind only this isolated test cursor: simulates a crash between M07 release and cursor commit.
        await using (var db = new QuantDeskDbContext(options))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE qd_exchange_v4_dev_checkpoints SET last_command_sequence=0 WHERE journal_epoch={epoch}");
        Check((await consumer.ProcessNextAsync()).LastCommandSequence == 1 && (await Hold(rejectBuy)).Version == releaseVersion,"release replay is idempotent");
        Check((await dispatcher.DispatchAsync(rejectSellCommand)).Record.Rejected?.RejectedSubmit.Equals(badSell)==true,"real rejected sell");
        Check((await consumer.ProcessNextAsync()).LastCommandSequence==2 && (await Hold(rejectSell)).ReservedShares==0 && (await Hold(rejectSell)).Status==EquityReservationStatus.Released,"shares released");
        await client.RegisterBookAsync(Guid.NewGuid(),book);
        Check((await dispatcher.DispatchAsync(rejectBuyCommand)).Equals(decisions[0]),"rejection immutable even after registration");
        Console.WriteLine("PASS: definitive buy/sell rejection releases, unknown and unreconciled outcomes retain holds, replay and retry safe");
        await dispatcher.PrepareAsync(seller.OwnerUserId,sellOneCommand,ExchangeWire.CreateOrder(session,instrument,sellOne,seller.Id,Side.Sell,149m,30));
        Check((await dispatcher.DispatchAsync(sellOneCommand)).Record.Accepted?.Submitted is not null,"first sell");
        await dispatcher.PrepareAsync(seller.OwnerUserId,sellTwoCommand,ExchangeWire.CreateOrder(session,instrument,sellTwo,seller.Id,Side.Sell,149m,70));
        Check((await dispatcher.DispatchAsync(sellTwoCommand)).Record.Accepted?.Submitted is not null,"second sell");
        await dispatcher.PrepareAsync(buyer.OwnerUserId,buyCommand,ExchangeWire.CreateOrder(session,instrument,buy,buyer.Id,Side.Buy,150m,40));
        var buyDecision = await dispatcher.DispatchAsync(buyCommand);
        Check(buyDecision.Record.Accepted?.Submitted?.Fills.Count==2,"two real FIFO fills");
        var rejectedCancel = Guid.NewGuid();
        await canceller.PrepareAsync(seller.OwnerUserId,rejectedCancel,sellOne);
        Check((await canceller.DispatchAsync(rejectedCancel)).Record.Rejected?.RejectedCancel is not null,"fully filled order cancel rejected");
        await canceller.PrepareAsync(seller.OwnerUserId,cancelCommand,sellTwo);
        Check((await canceller.DispatchAsync(cancelCommand)).Record.Accepted?.Cancelled is not null,"remaining sell cancelled");
        for (long seq=3;seq<=8;seq++)
            Check((await consumer.ProcessNextAsync()).LastCommandSequence==seq,"ordered mixed delivery "+seq);
        Check((await Hold(sellOne)).Status==EquityReservationStatus.Filled,"rejected cancellation does not release filled reservation");
        Check((await Hold(sellTwo)).Status==EquityReservationStatus.Released && (await Hold(sellTwo)).FilledQuantity==10,"partial cancellation releases only remainder");
        await dispatcher.PrepareAsync(buyer.OwnerUserId,pendingCommand,ExchangeWire.CreateOrder(session,instrument,pending,buyer.Id,Side.Buy,1m,1));
        Check(await client.GetCommandReceiptAsync(pendingCommand) is null,"prepared order still unknown");
        Check(!(await consumer.ProcessNextAsync()).AppliedCommand && (await Hold(pending)).ReservedCash==1m,"unknown remains reserved");
        await CheckAccounting();
        Console.WriteLine("PASS: mixed V4 FIFO settlement, rejected cancel no release, accepted partial cancel, cash/share conservation");
    }
    await using (var server=await DevServer.Start(Path.GetFullPath(args[0]),"--open-dev-journal",journal,epoch))
    {
        var dispatcher=new OutcomeOrderDispatcher(server.Client,options);
        var consumer=new OutcomeDeliveryConsumer(server.Client,options);
        Check((await dispatcher.DispatchAsync(rejectBuyCommand)).Record.Rejected is not null && (await Hold(rejectBuy)).Version==releaseVersion,"restart preserves rejection and release");
        Check((await consumer.ProcessNextAsync()).LastCommandSequence==8,"persistent cursor");
        // Deliberate invalid admission fixture; do not allow an unauthorised rejection to release anything.
        var rogue = ExchangeWire.CreateOrder(session,instrument+"-UNKNOWN",Guid.NewGuid(),buyer.Id,Side.Buy,1m,1);
        Check((await server.Client.SubmitOrderAsync(Guid.NewGuid(),rogue)).Record.Rejected is not null,"rogue rejection fixture");
        await MustReject(() => consumer.ProcessNextAsync(),"Rejection has no durably recorded authorization.");
        await using var db=new QuantDeskDbContext(options);
        await db.Database.OpenConnectionAsync();
        await using var query=new NpgsqlCommand("SELECT last_command_sequence FROM qd_exchange_v4_dev_checkpoints WHERE journal_epoch=@epoch",(NpgsqlConnection)db.Database.GetDbConnection());
        query.Parameters.AddWithValue("epoch",epoch);
        Check((long)(await query.ExecuteScalarAsync())! == 8,"unauthorized rejection does not advance cursor");
        await CheckAccounting();
        Console.WriteLine("PASS: restart, persistent cursor, unauthorized rejection fails closed");
    }
    Console.WriteLine("V4 FINANCIAL OUTCOME SMOKE PASSED");
}
finally
{
    try
    {
        await using var db = new QuantDeskDbContext(options);
        if (cancelTableReady) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM qd_exchange_v4_dev_cancellations WHERE journal_epoch = {epoch}");
        if (dispatchTableReady) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM qd_exchange_v4_dev_submissions WHERE journal_epoch = {epoch}");
        if (checkpointTableReady) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM qd_exchange_v4_dev_checkpoints WHERE journal_epoch = {epoch}");
        if (provisioned)
        {
            foreach (var a in new[] { buyer, seller })
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM equity_trade_settlements WHERE buyer_account_id = {a.Id} OR seller_account_id = {a.Id}");
            foreach (var a in new[] { buyer, seller })
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM equity_order_reservations WHERE account_id = {a.Id}");
            foreach (var a in new[] { buyer, seller })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM comptes_trading WHERE \"Id\" = {a.Id}");
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM utilisateurs WHERE id = {a.OwnerUserId}");
            }
        }
    }
    finally { Directory.Delete(directory, recursive: true); }
}

async Task<EquityOrderReservation> Hold(Guid id)
{
    await using var db = new QuantDeskDbContext(options);
    return await db.EquityOrderReservations.AsNoTracking().SingleAsync(r=>r.OrderId==id);
}
async Task CheckAccounting()
{
    await using var db = new QuantDeskDbContext(options);
    var b = await db.ComptesTrading.AsNoTracking().SingleAsync(a => a.Id == buyer.Id);
    var s = await db.ComptesTrading.AsNoTracking().SingleAsync(a => a.Id == seller.Id);
    Check(b.CashBalance == 94040m && s.CashBalance == 105960m, "cash conservation");
    Check(await db.PositionsAction.Where(p => p.CompteTradingId == b.Id && p.InstrumentId == instrument).Select(p => p.Quantity).SingleAsync() == 40, "buyer shares");
    Check(await db.PositionsAction.Where(p => p.CompteTradingId == s.Id && p.InstrumentId == instrument).Select(p => p.Quantity).SingleAsync() == 60, "seller shares");
    Check(await db.EquitySettlements.CountAsync(e => e.BuyerAccountId == b.Id && e.SellerAccountId == s.Id) == 2, "two settlement receipts");
    Check(await db.CashJournal.CountAsync(j => j.CompteTradingId == b.Id || j.CompteTradingId == s.Id) == 6, "two initial and four fill ledger entries");
    var reservations = await db.EquityOrderReservations.AsNoTracking().Where(r => r.AccountId == b.Id || r.AccountId == s.Id).ToListAsync();
    Check(reservations.Count == 6 && reservations.Where(r => r.OrderId != pending).All(r => r.ReservedCash == 0 && r.ReservedShares == 0), "all decided reservations exhausted/released");
    Check(reservations.Single(r => r.OrderId == pending).ReservedCash==1m,"unknown hold intact");
}
static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAIL: " + label); }
static async Task MustReject(Func<Task> action, string message)
{
    try { await action(); }
    catch (InvalidOperationException e) when (e.Message == message) { return; }
    throw new Exception("Expected rejection: " + message);
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
