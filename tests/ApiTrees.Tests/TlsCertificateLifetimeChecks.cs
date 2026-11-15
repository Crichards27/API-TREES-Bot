using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTrees.Core;

internal static class TlsCertificateLifetimeChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "api-trees-certificate-dates-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var expired in new[] { true, false })
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                var now = DateTimeOffset.UtcNow;
                using var generated = request.CreateSelfSigned(
                    now.AddDays(expired ? -14 : 1), now.AddDays(expired ? -7 : 7));
                // Reimport for Windows Schannel's persisted-key requirement.
                using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12),
                    null, X509KeyStorageFlags.Exportable);
                var path = Path.Combine(directory, expired ? "expired.crt" : "future.crt");
                await File.WriteAllTextAsync(path, certificate.ExportCertificatePem());
                await using var server = await LocalGameServer.StartAsync(certificate);
                using var client = new GameClient(server.Url, path);
                var rejected = false;
                try { await client.GetAsync<JsonElement>("/health", false); }
                catch (ConnectionException ex) when (!ex.Uncertain) { rejected = true; }
                check(rejected, expired ? "private trust rejects expired server certificates"
                    : "private trust rejects server certificates that are not yet valid");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
