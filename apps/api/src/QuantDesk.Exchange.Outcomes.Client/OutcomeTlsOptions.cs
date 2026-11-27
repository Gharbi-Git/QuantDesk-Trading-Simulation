using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QuantDesk.Exchange.Outcomes.Client;

// No system trust-store edits, no certificate-validation bypass, no HTTP fallback.
public sealed record OutcomeTlsOptions(string ServerCaFile, string ClientCertificateFile, string ClientKeyFile)
{
    public static OutcomeTlsOptions FromDirectory(string directory) => new(
        Path.Combine(directory, "server-ca.pem"), Path.Combine(directory, "backend.pem"), Path.Combine(directory, "backend-key.pem"));
}

internal sealed class OutcomeTlsConnection : IDisposable
{
    private readonly X509Certificate2 root;
    private readonly X509Certificate2 client;
    internal SocketsHttpHandler Handler { get; }
    internal OutcomeTlsConnection(OutcomeTlsOptions options)
    {
        root = X509Certificate2.CreateFromPem(File.ReadAllText(options.ServerCaFile));
        try
        {
            using var pem = X509Certificate2.CreateFromPemFile(options.ClientCertificateFile, options.ClientKeyFile);
            // Windows Schannel cannot always use ephemeral PEM private keys for client auth.
            // Temporary user-key storage (no PersistKeySet) is removed when disposed.
            var pfx = pem.Export(X509ContentType.Pkcs12);
            try { client = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            try
            {
                var chain = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck, // short-lived local test CA, no revocation service
                    DisableCertificateDownloads = true
                };
                chain.CustomTrustStore.Add(root);
                chain.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server authentication
                Handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        CertificateChainPolicy = chain,
                        ClientCertificates = new X509CertificateCollection { client },
                        LocalCertificateSelectionCallback = (_, _, _, _, _) => client,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                    }
                };
            }
            catch { client.Dispose(); throw; }
        }
        catch { root.Dispose(); throw; }
    }
    public void Dispose() { Handler.Dispose(); client.Dispose(); root.Dispose(); }
}
