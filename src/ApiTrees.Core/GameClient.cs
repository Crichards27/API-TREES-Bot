using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ApiTrees.Core;

public sealed class ApiException(int statusCode, string detail) : Exception($"HTTP {statusCode}: {detail}")
{
    public int StatusCode { get; } = statusCode;
    public string Detail { get; } = detail;
}

public sealed class ConnectionException(string message, bool uncertain, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Uncertain { get; } = uncertain;
}

public sealed class GameClient : IDisposable
{
    private readonly HttpClient http;
    public string Server { get; }
    public string? Token { get; set; }

    public GameClient(string server, string? caCertificate = null, bool insecure = false, TimeSpan? timeout = null)
        : this(server, CreateHandler(caCertificate, insecure), timeout) { }

    public GameClient(string server, HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        Server = NormalizeServer(server);
        http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    public static string NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use a full HTTP(S) server URL without credentials, query, or fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static HttpClientHandler CreateHandler(string? caCertificate, bool insecure)
    {
        if (insecure && !string.IsNullOrWhiteSpace(caCertificate))
            throw new ArgumentException("Choose a CA certificate or insecure mode, not both.");
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (insecure)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        else if (!string.IsNullOrWhiteSpace(caCertificate))
        {
            // Parse once, fail early, and capture only public certificate bytes.
            using var parsedRoot = X509Certificate2.CreateFromPemFile(caCertificate);
            var rootBytes = parsedRoot.RawData;
            handler.ServerCertificateCustomValidationCallback = (_, certificate, presentedChain, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                    SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var root = X509CertificateLoader.LoadCertificate(rootBytes);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (presentedChain is not null)
                    foreach (var element in presentedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                return chain.Build(certificate);
            };
        }
        return handler;
    }

    public async Task<T> CallAsync<T>(HttpMethod method, string path, object? body = null,
        bool auth = true, CancellationToken cancellationToken = default)
    {
        if (!path.StartsWith('/') || path.StartsWith("//")) throw new ArgumentException("Use a local API path.");
        var write = method != HttpMethod.Get;
        using var request = new HttpRequestMessage(method, Server + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("API-Trees-CSharp/2.0");
        if (auth)
        {
            if (string.IsNullOrWhiteSpace(Token)) throw new InvalidOperationException("Claim or resume a team first.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body,
                GameJson.Default.GetTypeInfo(body.GetType())!), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = raw.Length > 500 ? raw[..500] : raw;
                try
                {
                    using var error = JsonDocument.Parse(raw);
                    if (error.RootElement.TryGetProperty("detail", out var d)) detail = d.ToString();
                }
                catch (JsonException) { }
                if (write && (int)response.StatusCode >= 500)
                    throw new ConnectionException($"HTTP {(int)response.StatusCode}: {detail}. Action may have succeeded; refresh state before repeating.", true);
                throw new ApiException((int)response.StatusCode, detail);
            }
            try
            {
                if (typeof(T) == typeof(JsonElement))
                {
                    using var document = JsonDocument.Parse(raw);
                    return (T)(object)document.RootElement.Clone();
                }
                return (T)(JsonSerializer.Deserialize(raw, GameJson.Default.GetTypeInfo(typeof(T))!)
                    ?? throw new JsonException("Empty response."));
            }
            catch (JsonException ex)
            {
                throw new ConnectionException("Invalid JSON response. Check the server URL and routing." +
                    (write ? " Action may have succeeded; refresh state before repeating." : ""), write, ex);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ConnectionException("Connection failed: " + ex.Message +
                (write ? " Action may have succeeded; refresh state before repeating." : ""), write, ex);
        }
    }

    public Task<T> GetAsync<T>(string path, bool auth = true, CancellationToken ct = default) =>
        CallAsync<T>(HttpMethod.Get, path, auth: auth, cancellationToken: ct);
    public Task<JsonElement> ActAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default) =>
        CallAsync<JsonElement>(method, path, body, cancellationToken: ct);
    public void Dispose() => http.Dispose();
}
