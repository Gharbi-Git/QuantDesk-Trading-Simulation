using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Grpc.Core;
using Grpc.Net.Client;
using QuantDesk.Exchange.Client;
using QuantDesk.Exchange.Outcomes.Client;
using W = QuantDesk.Contracts.V1.Exchange;
using V = QuantDesk.Contracts.V2.Exchange;

if (args.Length != 2 || !File.Exists(args[0]) || !Directory.Exists(args[1])) throw new ArgumentException("Expected native executable and fresh temporary directory");
var exe = Path.GetFullPath(args[0]); var dir = Path.GetFullPath(args[1]);
if (Directory.EnumerateFileSystemEntries(dir).Any()) throw new InvalidOperationException("Use an empty temporary directory");
using var serverCa = Root("QuantDesk test server CA");
using var clientCa = Root("QuantDesk test client CA");
using var alienCa = Root("QuantDesk unrelated CA");
File.WriteAllText(Path.Combine(dir,"server-ca.pem"),serverCa.ExportCertificatePem());
File.WriteAllText(Path.Combine(dir,"client-ca.pem"),clientCa.ExportCertificatePem());
File.WriteAllText(Path.Combine(dir,"alien-ca.pem"),alienCa.ExportCertificatePem());
Leaf(dir,"server",serverCa,true,true);
Leaf(dir,"backend",clientCa,false,true);
// Same subject and issuing CA but different leaf/key: must fail the exact certificate allowlist.
Leaf(dir,"other",clientCa,false,true);
Leaf(dir,"alien",alienCa,false,true);
var wrongDir=Directory.CreateDirectory(Path.Combine(dir,"wrong-name")).FullName;
foreach(var f in new[]{"server-ca.pem","client-ca.pem","backend.pem","backend-key.pem"})File.Copy(Path.Combine(dir,f),Path.Combine(wrongDir,f));
Leaf(wrongDir,"server",serverCa,true,false);
var tls=OutcomeTlsOptions.FromDirectory(dir);
var epoch=Guid.NewGuid();var journal=Path.Combine(dir,"journal");
var book=new W.BookKey{SessionId=Guid.NewGuid().ToString("D"),InstrumentId="MTLS-TEST"};
var seller=Guid.NewGuid();var buyer=Guid.NewGuid();
var buy=ExchangeWire.CreateOrder(Guid.Parse(book.SessionId),book.InstrumentId,Guid.NewGuid(),buyer,W.Side.Buy,150m,10);
var rejectId=Guid.NewGuid(); var fillId=Guid.NewGuid();
V.CommandReceipt rejected, filled;
await using(var server=await Server.Start(exe,"--create-secure-journal",journal,epoch,dir))
{
    using var c=new OutcomeExchangeClient(tls,server.Port,epoch);
    Check((await c.GetStatusAsync()).HighWatermark==0,"fresh authenticated connection");
    // Negative probes all attempt a mutating RPC. Check that none reaches the journal.
    using(var plain=new OutcomeExchangeClient(server.Port,epoch,TimeSpan.FromSeconds(5)))
        await Denied(()=>plain.RegisterBookAsync(Guid.NewGuid(),book),"plaintext");
    using(var root=X509Certificate2.CreateFromPem(File.ReadAllText(tls.ServerCaFile)))
    using(var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,SslOptions=new SslClientAuthenticationOptions{CertificateChainPolicy=Policy(root)}})
    using(var channel=GrpcChannel.ForAddress($"https://127.0.0.1:{server.Port}",new GrpcChannelOptions{HttpHandler=handler}))
    {
        var raw=new V.ExchangeService.ExchangeServiceClient(channel);
        await Denied(async()=>{_ = await raw.RegisterBookAsync(new W.RegisterBookRequest{Identity=new W.CommandIdentity{ExpectedJournalEpoch=epoch.ToString("D"),CommandId=Guid.NewGuid().ToString("D")},Book=book},deadline:DateTime.UtcNow.AddSeconds(5));},"missing client certificate");
    }
    using(var alien=new OutcomeExchangeClient(tls with{ClientCertificateFile=Path.Combine(dir,"alien.pem"),ClientKeyFile=Path.Combine(dir,"alien-key.pem")},server.Port,epoch,TimeSpan.FromSeconds(5)))
        await Denied(()=>alien.RegisterBookAsync(Guid.NewGuid(),book),"untrusted client CA");
    using(var other=new OutcomeExchangeClient(tls with{ClientCertificateFile=Path.Combine(dir,"other.pem"),ClientKeyFile=Path.Combine(dir,"other-key.pem")},server.Port,epoch,TimeSpan.FromSeconds(5)))
        await Denied(()=>other.RegisterBookAsync(Guid.NewGuid(),book),"same CA, unlisted leaf",StatusCode.PermissionDenied);
    using(var wrong=new OutcomeExchangeClient(tls with{ServerCaFile=Path.Combine(dir,"alien-ca.pem")},server.Port,epoch,TimeSpan.FromSeconds(5)))
        await Denied(()=>wrong.RegisterBookAsync(Guid.NewGuid(),book),"untrusted server CA");
    Check((await c.GetStatusAsync()).HighWatermark==0,"unauthorized commands leave journal unchanged");
    Console.WriteLine("PASS: plaintext, missing/untrusted client, unlisted client leaf and untrusted server blocked; no journal effects");
    rejected=await c.SubmitOrderAsync(rejectId,buy);
    Check(rejected.Record.Rejected?.RejectedSubmit.Equals(buy)==true,"durable rejection over TLS");
    Check((await c.RegisterBookAsync(Guid.NewGuid(),book)).Record.Accepted?.Registered is not null,"registration");
    var sell=ExchangeWire.CreateOrder(Guid.Parse(book.SessionId),book.InstrumentId,Guid.NewGuid(),seller,W.Side.Sell,149m,20);
    Check((await c.SubmitOrderAsync(Guid.NewGuid(),sell)).Record.Accepted?.Submitted is not null,"sell");
    var good=buy.Clone();good.OrderId=Guid.NewGuid().ToString("D");
    filled=await c.SubmitOrderAsync(fillId,good);
    var trade=filled.Record.Accepted.Submitted.Fills.Single();
    Check(trade.Quantity==10&&trade.ExecutionPriceUnits==1490000,"actual match over TLS");
    Check((await c.CancelOrderAsync(Guid.NewGuid(),book,Guid.Parse(sell.OrderId),seller)).Record.Accepted?.Cancelled is not null,"cancel over TLS");
    Check((await c.GetCommandReceiptAsync(rejectId))?.Equals(rejected)==true,"receipt");
    Check((await c.GetDeliveryBatchAsync(0)).Records.Count==5,"complete decision stream");
    Console.WriteLine("PASS: all six V2 RPCs, real matching, rejection and cancellation over mTLS");
}
await using(var server=await Server.Start(exe,"--open-secure-journal",journal,epoch,dir))
{
    using var c=new OutcomeExchangeClient(tls,server.Port,epoch);
    Check((await c.SubmitOrderAsync(rejectId,buy)).Equals(rejected),"durable retry");
    Check((await c.GetCommandReceiptAsync(fillId))?.Equals(filled)==true,"durable fill identity/time");
    Check((await c.GetStatusAsync()).HighWatermark==5,"restart no duplicate");
    Console.WriteLine("PASS: encrypted restart and durable receipt recovery");
}
var wrongEpoch=Guid.NewGuid();
await using(var server=await Server.Start(exe,"--create-secure-journal",Path.Combine(wrongDir,"journal"),wrongEpoch,wrongDir))
{
    using var c=new OutcomeExchangeClient(tls,server.Port,wrongEpoch,TimeSpan.FromSeconds(5));
    await Denied(()=>c.RegisterBookAsync(Guid.NewGuid(),book),"server name mismatch despite trusted CA");
    Console.WriteLine("PASS: trusted CA with incorrect server SAN blocked");
}
// A secure invocation missing credentials must exit, not bind a plaintext listener.
var missing=Directory.CreateDirectory(Path.Combine(dir,"missing")).FullName;
using(var p=Process.Start(Server.Info(exe,"--create-secure-journal",Path.Combine(missing,"journal"),Guid.NewGuid(),missing))??throw new Exception("start failed"))
{
    try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(p.ExitCode!=0&&!File.Exists(Path.Combine(missing,"journal")),"missing TLS startup fails closed"); }
    finally {if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}}
}
Console.WriteLine("MTLS V2 SECURITY SMOKE PASSED");

static X509ChainPolicy Policy(X509Certificate2 root)
{
    var p=new X509ChainPolicy{TrustMode=X509ChainTrustMode.CustomRootTrust,RevocationMode=X509RevocationMode.NoCheck,DisableCertificateDownloads=true};p.CustomTrustStore.Add(root);p.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));return p;
}
static X509Certificate2 Root(string name)
{
    using var key=RSA.Create(2048);
    var req=new CertificateRequest("CN="+name,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
    req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey,false));
    return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10),DateTimeOffset.UtcNow.AddDays(2));
}
static void Leaf(string dir,string name,X509Certificate2 issuer,bool server,bool goodName)
{
    using var key=RSA.Create(2048);
    var req=new CertificateRequest(server?"CN=quantdesk-exchange":"CN=quantdesk-backend",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid(server?"1.3.6.1.5.5.7.3.1":"1.3.6.1.5.5.7.3.2")},true));
    req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey,false));
    if(server){var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(System.Net.IPAddress.Parse(goodName?"127.0.0.1":"127.0.0.2"));req.CertificateExtensions.Add(san.Build());}
    var serial=RandomNumberGenerator.GetBytes(16);serial[0]&=0x7f;serial[15]|=1;
    using var cert=req.Create(issuer,DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddHours(12),serial);
    File.WriteAllText(Path.Combine(dir,name+".pem"),cert.ExportCertificatePem()+"\n");
    File.WriteAllText(Path.Combine(dir,name+"-key.pem"),key.ExportPkcs8PrivateKeyPem()+"\n");
}
static void Check(bool b,string label){if(!b)throw new InvalidOperationException("FAIL: "+label);}
static async Task Denied(Func<Task> call,string label,StatusCode? expected=null)
{
    try{await call();}
    catch(RpcException e) when(expected is not null?e.StatusCode==expected.Value:e.StatusCode is StatusCode.Unavailable or StatusCode.Internal or StatusCode.Unauthenticated or StatusCode.PermissionDenied){return;}
    throw new InvalidOperationException("Unauthorized RPC was not rejected as expected: "+label);
}
sealed class Server : IAsyncDisposable
{
    private readonly Process process;
    public int Port {get;}
    private Server(Process p,int port){process=p;Port=port;}
    public static ProcessStartInfo Info(string exe,string mode,string journal,Guid epoch,string dir)
    {
        var s=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true};
        foreach(var a in new[]{mode,journal,epoch.ToString("D"),dir})s.ArgumentList.Add(a);return s;
    }
    public static async Task<Server> Start(string exe,string mode,string journal,Guid epoch,string dir)
    {
        var p=Process.Start(Info(exe,mode,journal,epoch,dir))??throw new Exception("start failed");
        try{var line=await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));var m=Regex.Match(line??"",@"^MTLS V2 ONLY: authenticated exchange at 127\.0\.0\.1:(\d+); journal V4$");if(!m.Success||!int.TryParse(m.Groups[1].Value,out var port))throw new InvalidOperationException("Unexpected secure startup: "+line);return new(p,port);}
        catch{await Stop(p);throw;}
    }
    public async ValueTask DisposeAsync()=>await Stop(process);
    private static async Task Stop(Process p){try{if(!p.HasExited)p.Kill(true);await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}finally{p.Dispose();}}
}
