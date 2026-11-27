using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Api.ExchangeIntegration;
using QuantDesk.Contracts.V1.Exchange;
using QuantDesk.Exchange.Client;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Application.Contracts;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;
using Client = QuantDesk.Exchange.Client.IExchangeClient;

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
var reserve = new PortfolioReservationService(options);
var settle = new ReservedEquitySettlementService(options);
var provisioned = false;
var checkpointTableReady = false;
Directory.CreateDirectory(directory);
try
{
    await using (var db = new QuantDeskDbContext(options))
    {
        await db.Database.MigrateAsync();
        // Deliberately test-only schema. No changes to the shared EF model/snapshot.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS qd_exchange_dev_checkpoints (journal_epoch uuid PRIMARY KEY, last_command_sequence bigint NOT NULL CHECK (last_command_sequence >= 0))");
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
    await using (var server = await DevServer.Start(Path.GetFullPath(args[0]), "--create-dev-journal", journal, epoch.ToString("D")))
    {
        var client = server.Client;
        Check((await client.GetStatusAsync()).HighWatermark == 0, "fresh epoch");
        await using (var db = new QuantDeskDbContext(options))
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO qd_exchange_dev_checkpoints (journal_epoch, last_command_sequence) VALUES ({epoch}, 0)");
        // This isolated fixture supplies trusted session/instrument/risk authorization.
        // The dispatcher must still verify owner and availability through M07.
        var dispatcher = new DevelopmentOrderDispatcher(client, options);
        await dispatcher.ProvisionTestTableAsync();
        dispatchTableReady = true;
        await client.RegisterBookAsync(Guid.NewGuid(), book);
        var unauthorized = Guid.NewGuid();
        var unaffordable = Guid.NewGuid();
        await MustReject(() => dispatcher.PrepareAsync(seller.OwnerUserId, unauthorized,
            ExchangeWire.CreateOrder(session, instrument, Guid.NewGuid(), buyer.Id, Side.Buy, 1m, 1)), "Trading account not found for owner.");
        await MustReject(() => dispatcher.PrepareAsync(buyer.OwnerUserId, unaffordable,
            ExchangeWire.CreateOrder(session, instrument, Guid.NewGuid(), buyer.Id, Side.Buy, 100001m, 1)), "Insufficient available cash for reservation.");
        await MustReject(() => dispatcher.DispatchAsync(unauthorized), "Reservation not confirmed; dispatch prohibited.");
        await MustReject(() => dispatcher.DispatchAsync(unaffordable), "Reservation not confirmed; dispatch prohibited.");
        Check((await client.GetStatusAsync()).HighWatermark == 1, "denied orders never reach exchange");
        var firstOrder = ExchangeWire.CreateOrder(session, instrument, sellOne, seller.Id, Side.Sell, 149m, 30);
        await dispatcher.PrepareAsync(seller.OwnerUserId, sellOneCommand, firstOrder);
        // Simulate crash after M07 commit but before readiness commit, by resetting
        // the isolated fixture's readiness marker before any dispatch attempt.
        await using (var db = new QuantDeskDbContext(options))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE qd_exchange_dev_submissions SET phase=0 WHERE command_id={sellOneCommand}");
        await new DevelopmentOrderDispatcher(client, options).PrepareAsync(seller.OwnerUserId, sellOneCommand, firstOrder);
        var conflict = firstOrder.Clone(); conflict.Quantity = 29;
        await MustReject(() => dispatcher.PrepareAsync(seller.OwnerUserId, sellOneCommand, conflict), "Command payload or owner conflict.");
        var receipts = await Task.WhenAll(dispatcher.DispatchAsync(sellOneCommand), new DevelopmentOrderDispatcher(client, options).DispatchAsync(sellOneCommand));
        Check(receipts.All(r => r.CommandSequence == 2), "concurrent duplicate returns one receipt");
        await dispatcher.PrepareAsync(seller.OwnerUserId, sellTwoCommand,
            ExchangeWire.CreateOrder(session, instrument, sellTwo, seller.Id, Side.Sell, 149m, 70));
        Check((await dispatcher.DispatchAsync(sellTwoCommand)).CommandSequence == 3, "second sell accepted");
        var buyOrder = ExchangeWire.CreateOrder(session, instrument, buy, buyer.Id, Side.Buy, 150m, 40);
        var lostReply = new DevelopmentOrderDispatcher(new LoseSubmitReply(client), options);
        await lostReply.PrepareAsync(buyer.OwnerUserId, buyCommand, buyOrder);
        try { await lostReply.DispatchAsync(buyCommand); throw new Exception("Expected lost reply"); }
        catch (IOException e) when (e.Message == "Simulated lost accepted reply") { }
        await using (var db = new QuantDeskDbContext(options))
        {
            var hold = await db.EquityOrderReservations.AsNoTracking().SingleAsync(r => r.OrderId == buy);
            Check(hold.ReservedCash == 6000m && hold.Status == EquityReservationStatus.Active, "ambiguous reply preserves cash hold");
            await db.Database.OpenConnectionAsync();
            await using var query = new NpgsqlCommand("SELECT phase FROM qd_exchange_dev_submissions WHERE command_id=@id", (NpgsqlConnection)db.Database.GetDbConnection());
            query.Parameters.AddWithValue("id", buyCommand);
            Check((int)(await query.ExecuteScalarAsync())! == 1, "ambiguous reply preserves ready intent");
        }
        Check((await new DevelopmentOrderDispatcher(client, options).DispatchAsync(buyCommand)).CommandSequence == 4, "lost reply recovers exact accepted command");
        Check((await client.GetStatusAsync()).HighWatermark == 4, "no duplicate journal append");
        Console.WriteLine("PASS: durable intent, owner and funds checks, reservation recovery, concurrent dispatch and lost reply recovery");
        // Cancellation is still an explicitly authorized test fixture, not a durable cancel dispatcher.
        await client.CancelOrderAsync(Guid.NewGuid(), book, sellTwo, seller.Id);
        var consumer = new ExchangeDeliveryConsumer(client, options);
        // Two separate consumers must serialize against the same persistent cursor.
        var concurrent = await Task.WhenAll(consumer.ProcessNextAsync(), new ExchangeDeliveryConsumer(client, options).ProcessNextAsync());
        Check(concurrent.Select(x => x.LastCommandSequence).Order().SequenceEqual(new long[] { 1, 2 }), "serialized cursor updates");
        Check((await consumer.ProcessNextAsync()).LastCommandSequence == 3, "resting orders consumed");
        var delivery = await client.GetDeliveryBatchAsync(3, 1);
        var fills = delivery.Records.Single().Submitted.Fills;
        Check(fills.Count == 2 && fills[0].Quantity == 30 && fills[1].Quantity == 10, "actual FIFO two-fill command");
        // Simulate a crash after the first settlement commits but before the
        // command checkpoint advances. No fake fill: use actual engine payload.
        var first = ExchangeWire.MapFill(fills[0]);
        var prior = await settle.SettleAsync(new TrustedReservedEquityExecution(first.ExecutionId, first.BuyerAccountId,
            first.SellerAccountId, first.InstrumentId, first.Quantity, first.ExecutionPrice, first.BuyOrderId, first.SellOrderId, first.SessionId));
        Check(!prior.AlreadySettled, "first fill committed before checkpoint");
        Check((await new ExchangeDeliveryConsumer(client, options).ProcessNextAsync()).LastCommandSequence == 4, "retry completes both fills");
        // Same replay window for cancellation. Release is idempotent.
        await reserve.ReleaseConfirmedAsync(seller.OwnerUserId, seller.Id, session, sellTwo);
        Check((await consumer.ProcessNextAsync()).LastCommandSequence == 5, "cancellation replay advances cursor");
        await CheckAccounting();
        Console.WriteLine("PASS: real C++ FIFO fills -> M07 PostgreSQL settlement; cash/share conservation, reservation release, replay without duplicate ledger entries");
    }
    await using (var server = await DevServer.Start(Path.GetFullPath(args[0]), "--open-dev-journal", journal, epoch.ToString("D")))
    {
        var recovered = await new DevelopmentOrderDispatcher(server.Client, options).DispatchAsync(buyCommand);
        Check(recovered.CommandSequence == 4, "accepted durable dispatch survives engine restart and settlement");
        var consumer = new ExchangeDeliveryConsumer(server.Client, options);
        var end = await consumer.ProcessNextAsync();
        Check(!end.AppliedCommand && end.LastCommandSequence == 5 && end.HighWatermark == 5, "persistent cursor after restart");
        await CheckAccounting();
        // Negative fixture: deliberately bypass reservation only in isolated test.
        // Consumer must refuse this journal record and leave cursor unchanged.
        await server.Client.SubmitOrderAsync(Guid.NewGuid(), ExchangeWire.CreateOrder(session, instrument, Guid.NewGuid(), buyer.Id, Side.Buy, 1m, 1));
        for (var i = 0; i < 2; i++)
        {
            try { await consumer.ProcessNextAsync(); throw new Exception("Missing reservation was accepted"); }
            catch (InvalidOperationException e) when (e.Message == "Delivery has no matching reservation.") { }
        }
        await using var db = new QuantDeskDbContext(options);
        await db.Database.OpenConnectionAsync();
        await using var query = new NpgsqlCommand("SELECT last_command_sequence FROM qd_exchange_dev_checkpoints WHERE journal_epoch = @epoch", (NpgsqlConnection)db.Database.GetDbConnection());
        query.Parameters.AddWithValue("epoch", epoch);
        Check((long)(await query.ExecuteScalarAsync())! == 5, "failed record never advances cursor");
        await CheckAccounting();
        Console.WriteLine("PASS: engine restart, persistent cursor, missing reservation fails closed");
    }
    Console.WriteLine("DURABLE ORDER DISPATCH SMOKE PASSED");
}
finally
{
    try
    {
        await using var db = new QuantDeskDbContext(options);
        if (dispatchTableReady) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM qd_exchange_dev_submissions WHERE journal_epoch = {epoch}");
        if (checkpointTableReady) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM qd_exchange_dev_checkpoints WHERE journal_epoch = {epoch}");
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

async Task CheckAccounting()
{
    await using var db = new QuantDeskDbContext(options);
    var b = await db.ComptesTrading.AsNoTracking().SingleAsync(a => a.Id == buyer.Id);
    var s = await db.ComptesTrading.AsNoTracking().SingleAsync(a => a.Id == seller.Id);
    Check(b.CashBalance == 94040m && s.CashBalance == 105960m && b.CashBalance + s.CashBalance == 200000m, "cash conservation");
    Check(await db.PositionsAction.Where(p => p.CompteTradingId == b.Id && p.InstrumentId == instrument).Select(p => p.Quantity).SingleAsync() == 40, "buyer shares");
    Check(await db.PositionsAction.Where(p => p.CompteTradingId == s.Id && p.InstrumentId == instrument).Select(p => p.Quantity).SingleAsync() == 60, "seller shares");
    Check(await db.EquitySettlements.CountAsync(e => e.BuyerAccountId == b.Id && e.SellerAccountId == s.Id) == 2, "two settlement receipts");
    Check(await db.CashJournal.CountAsync(j => j.CompteTradingId == b.Id || j.CompteTradingId == s.Id) == 6, "two initial and four fill ledger entries");
    var reservations = await db.EquityOrderReservations.AsNoTracking().Where(r => r.AccountId == b.Id || r.AccountId == s.Id).ToListAsync();
    Check(reservations.Count == 3 && reservations.All(r => r.ReservedCash == 0 && r.ReservedShares == 0), "all reservations exhausted/released");
    Check(reservations.Single(r => r.OrderId == sellTwo).Status == EquityReservationStatus.Released && reservations.Single(r => r.OrderId == sellTwo).FilledQuantity == 10, "partial seller cancellation");
}
static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAIL: " + label); }
static async Task MustReject(Func<Task> action, string message)
{
    try { await action(); }
    catch (InvalidOperationException e) when (e.Message == message) { return; }
    throw new Exception("Expected rejection: " + message);
}
// Test double only drops an actual accepted network reply; never fabricates a fill.
sealed class LoseSubmitReply(Client inner) : Client
{
    public Guid JournalEpoch => inner.JournalEpoch;
    public Task<StatusReply> GetStatusAsync(CancellationToken ct = default) => inner.GetStatusAsync(ct);
    public Task<AcceptedReceipt> RegisterBookAsync(Guid id, BookKey book, CancellationToken ct = default) => inner.RegisterBookAsync(id, book, ct);
    public async Task<AcceptedReceipt> SubmitOrderAsync(Guid id, EquityLimitOrder order, CancellationToken ct = default)
    {
        await inner.SubmitOrderAsync(id, order, ct);
        throw new IOException("Simulated lost accepted reply");
    }
    public Task<AcceptedReceipt> CancelOrderAsync(Guid id, BookKey book, Guid order, Guid account, CancellationToken ct = default) => inner.CancelOrderAsync(id, book, order, account, ct);
    public Task<AcceptedReceipt?> GetCommandReceiptAsync(Guid id, CancellationToken ct = default) => inner.GetCommandReceiptAsync(id, ct);
    public Task<DeliveryBatch> GetDeliveryBatchAsync(long after, uint max = 64, CancellationToken ct = default) => inner.GetDeliveryBatchAsync(after, max, ct);
}
sealed class DevServer : IAsyncDisposable
{
    private readonly Process process;
    private readonly GrpcExchangeClient channel;
    private readonly int port;
    public Client Client { get; }
    private DevServer(Process process, int port, Guid epoch) { this.process = process; this.port = port; channel = new GrpcExchangeClient(port, epoch); Client = channel; }
    public GrpcExchangeClient CreateClient(Guid epoch) => new(port, epoch);
    public static async Task<DevServer> Start(string exe, string mode, string journal, string epoch)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add(mode); start.ArgumentList.Add(journal); start.ArgumentList.Add(epoch);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Server failed to start");
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var match = Regex.Match(line ?? "", @"^DEV ONLY: unencrypted exchange at 127\.0\.0\.1:(\d+); no risk checks or settlement$");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var port) || port < 1 || port > 65535) throw new InvalidOperationException("Unexpected server startup: " + line);
            return new DevServer(process, port, Guid.Parse(epoch));
        }
        catch { await Stop(process); throw; }
    }
    public async ValueTask DisposeAsync() { channel.Dispose(); await Stop(process); }
    private static async Task Stop(Process process)
    {
        try
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) when (process.HasExited) { } }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { process.Dispose(); }
    }
}
