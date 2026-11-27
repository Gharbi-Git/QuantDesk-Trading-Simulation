using System.Diagnostics;
using System.Text.RegularExpressions;
using Grpc.Core;
using Grpc.Net.Client;
using QuantDesk.Contracts.V1.Exchange;
using Client = QuantDesk.Contracts.V1.Exchange.ExchangeService.ExchangeServiceClient;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the path to qd_exchange_trading.exe");
var executable = Path.GetFullPath(args[0]);
var directory = Path.Combine(Path.GetTempPath(), "qd-client-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var journal = Path.Combine(directory, "journal");
var epoch = Id();
var book = new BookKey { SessionId = Id(), InstrumentId = "TEST-EQUITY" };
var seller = Id(); var buyer = Id();
var sellId = Id(); var buyId = Id();
var register = new RegisterBookRequest { Identity = Identity(), Book = book.Clone() };
var sell = new SubmitOrderRequest { Identity = Identity(), Order = Order(sellId, seller, Side.Sell, 1_490_000, 100) };
var buy = new SubmitOrderRequest { Identity = Identity(), Order = Order(buyId, buyer, Side.Buy, 1_500_000, 40) };
var cancel = new CancelOrderRequest { Identity = Identity(), Book = book.Clone(), AccountId = seller, OrderId = sellId };
string executionId;
long timestamp;
try
{
    await using (var server = await DevServer.Start(executable, "--create-dev-journal", journal, epoch))
    {
        var client = server.Client;
        var status = await client.GetStatusAsync(new StatusRequest(), deadline: Deadline());
        Check(status.Healthy && status.JournalEpoch == epoch && status.HighWatermark == 0, "initial status");
        Receipt(await client.RegisterBookAsync(register, deadline: Deadline()), register.Identity, 1);
        Receipt(await client.SubmitOrderAsync(sell, deadline: Deadline()), sell.Identity, 2);
        Receipt(await client.SubmitOrderAsync(buy, deadline: Deadline()), buy.Identity, 3);
        Receipt(await client.SubmitOrderAsync(buy, deadline: Deadline()), buy.Identity, 3);
        var conflict = buy.Clone(); conflict.Order.Quantity++;
        await Expect(StatusCode.AlreadyExists, async () => { await client.SubmitOrderAsync(conflict, deadline: Deadline()); });
        var wrongEpoch = buy.Clone(); wrongEpoch.Identity.ExpectedJournalEpoch = Id();
        await Expect(StatusCode.FailedPrecondition, async () => { await client.SubmitOrderAsync(wrongEpoch, deadline: Deadline()); });
        Receipt(await client.CancelOrderAsync(cancel, deadline: Deadline()), cancel.Identity, 4);
        var known = await client.GetCommandReceiptAsync(new ReceiptRequest { Identity = buy.Identity.Clone() }, deadline: Deadline());
        Receipt(known.Receipt, buy.Identity, 3);
        var missing = await client.GetCommandReceiptAsync(new ReceiptRequest { Identity = Identity() }, deadline: Deadline());
        Check(missing.Receipt is null, "missing receipt is absent");
        var first = await Page(client, 0, 2);
        Check(first.HighWatermark == 4 && first.Records.Count == 2 && first.Records[0].Registered?.Book.Equals(book) == true && first.Records[1].Submitted?.Order.Equals(sell.Order) == true, "first page");
        var second = await Page(client, 2, 2);
        Check(second.Records.Count == 2 && second.Records[1].Cancelled?.OrderId == sellId && second.Records[1].Cancelled?.AccountId == seller, "cancel delivery");
        var submitted = second.Records[0].Submitted;
        Check(submitted is not null && submitted.State == OrderState.Filled && submitted.FilledQuantity == 40 && submitted.OpenQuantity == 0 && submitted.Fills.Count == 1, "fill count and order state");
        var fill = submitted!.Fills[0];
        Check(fill.Book.Equals(book) && fill.BuyOrderId == buyId && fill.SellOrderId == sellId && fill.BuyerAccountId == buyer && fill.SellerAccountId == seller && fill.BookExecutionSequence == 1 && fill.ExecutionPriceUnits == 1_490_000 && fill.Quantity == 40, "execution fields");
        Check(Guid.TryParseExact(fill.ExecutionId, "D", out var id) && id != Guid.Empty && fill.ExecutionTimeUtcUnixMicros > 0, "execution identity and UTC timestamp");
        executionId = fill.ExecutionId; timestamp = fill.ExecutionTimeUtcUnixMicros;
        Check((await Page(client, 4, 1)).Records.Count == 0, "end cursor");
        Console.WriteLine("PASS: C# -> C++ all six RPCs, real matching, exact price, duplicate and epoch protection");
    }
    await using (var server = await DevServer.Start(executable, "--open-dev-journal", journal, epoch))
    {
        var replay = await Page(server.Client, 2, 1);
        Check(replay.HighWatermark == 4 && replay.Records.Count == 1 && replay.Records[0].Submitted.Fills.Count == 1, "recovered page");
        var fill = replay.Records[0].Submitted.Fills[0];
        Check(fill.ExecutionId == executionId && fill.ExecutionTimeUtcUnixMicros == timestamp, "restart preserves identity/time");
        Receipt(await server.Client.SubmitOrderAsync(buy, deadline: Deadline()), buy.Identity, 3);
        Console.WriteLine("PASS: reopened journal, stable execution ID/time, idempotent retry");
    }
    Console.WriteLine("CROSS-LANGUAGE EXCHANGE SMOKE PASSED");
}
finally { Directory.Delete(directory, recursive: true); }

static string Id() => Guid.NewGuid().ToString("D");
static DateTime Deadline() => DateTime.UtcNow.AddSeconds(10);
static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAIL: " + label); }
CommandIdentity Identity() => new() { ExpectedJournalEpoch = epoch, CommandId = Id() };
EquityLimitOrder Order(string id, string account, Side side, long price, long quantity) => new() { Book = book.Clone(), OrderId = id, AccountId = account, Side = side, LimitPriceUnits = price, Quantity = quantity };
void Receipt(AcceptedReceipt? receipt, CommandIdentity identity, long sequence) => Check(receipt is not null && receipt.JournalEpoch == epoch && receipt.CommandId == identity.CommandId && receipt.CommandSequence == sequence, "receipt " + sequence);
async Task<DeliveryBatch> Page(Client client, long after, uint count)
{
    var result = await client.GetDeliveryBatchAsync(new DeliveryRequest { ExpectedJournalEpoch = epoch, AfterCommandSequence = after, MaxCommands = count }, deadline: Deadline());
    Check(result.JournalEpoch == epoch && result.HighWatermark >= after && result.Records.Count <= count, "page envelope");
    for (var i = 0; i < result.Records.Count; ++i) Check(result.Records[i].CommandSequence == after + i + 1, "contiguous delivery");
    return result;
}
static async Task Expect(StatusCode code, Func<Task> action)
{
    try { await action(); }
    catch (RpcException e) when (e.StatusCode == code) { return; }
    throw new InvalidOperationException("Expected RPC error " + code);
}

sealed class DevServer : IAsyncDisposable
{
    private readonly Process process;
    private readonly GrpcChannel channel;
    public Client Client { get; }
    private DevServer(Process process, GrpcChannel channel) { this.process = process; this.channel = channel; Client = new Client(channel); }
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
            var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 1024 * 1024, MaxSendMessageSize = 64 * 1024 });
            return new DevServer(process, channel);
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
