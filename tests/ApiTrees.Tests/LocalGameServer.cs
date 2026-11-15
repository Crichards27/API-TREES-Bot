using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// Real loopback HTTPS, isolated from LAN games and the machine's trust store.
internal sealed class LocalGameServer(WebApplication app) : IAsyncDisposable
{
    public string Url => app.Urls.Single();
    public string Status { get; set; } = "lobby";
    public int GameId { get; set; } = 7;
    public bool FailHealth { get; set; }
    public bool FailWrite { get; set; }
    public bool NoRound { get; set; }
    public int? ResultsGameId { get; set; }
    public bool ReplaceRoundOnSelect { get; set; }
    public bool FailTeamsAfterSelect { get; set; }
    public bool FailSnapshot { get; set; }
    public int Writes { get; private set; }
    private readonly Dictionary<string, int> tokenRounds = [];
    public bool Claimed => tokenRounds.Values.Contains(GameId);

    public static X509Certificate2 Certificate(string name = "localhost", bool authority = false,
        X509Certificate2? issuer = null, bool serverAuthentication = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(authority
            ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        if (authority && !serverAuthentication)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        if (!authority)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new(serverAuthentication ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(name);
            if (name == "localhost") names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var signed = issuer is null
            ? request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7))
            : request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.HasPrivateKey ? null : signed.CopyWithPrivateKey(key);
        // Schannel cannot serve ephemeral keys. Import without EphemeralKeySet;
        // disposal removes its temporary key container (no trust-store changes).
        return X509CertificateLoader.LoadPkcs12((withKey ?? signed).Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.Exportable);
    }

    public static async Task<LocalGameServer> StartAsync(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var server = new LocalGameServer(app);
        app.Run(server.RespondAsync);
        await app.StartAsync();
        return server;
    }

    private async Task RespondAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (NoRound && path == "/teams" || path == "/results" && ResultsGameId is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{}");
            return;
        }
        if (path == "/health" && FailHealth) { context.Response.StatusCode = 503; return; }
        if (path == "/teams" && FailTeamsAfterSelect && tokenRounds.Count > 0 ||
            path == "/me/inventory" && FailSnapshot) { context.Response.StatusCode = 503; return; }
        // Like pinned upstream, bearer tokens identify a team in their own round,
        // including old rounds. /me deliberately does not return that round ID.
        var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal);
        var authenticated = tokenRounds.TryGetValue(token, out var tokenRound);
        if (path is "/me" or "/me/inventory" or "/me/plots" or "/trees" or "/market/buy" && !authenticated)
        { context.Response.StatusCode = 401; return; }
        if (path == "/market/buy" && (tokenRound != GameId || Status != "active"))
        { context.Response.StatusCode = 409; return; }
        string? issuedToken = null;
        if (context.Request.Method != "GET")
        {
            Writes++;
            if (FailWrite) { context.Response.StatusCode = 500; await context.Response.WriteAsync("Write outcome unknown"); return; }
            if (path == "/teams/select")
            {
                if (ReplaceRoundOnSelect) { GameId++; ReplaceRoundOnSelect = false; }
                if (Status != "lobby" || Claimed) { context.Response.StatusCode = 409; return; }
                issuedToken = "local-test-token-" + GameId;
                tokenRounds.Add(issuedToken, GameId);
            }
        }
        var json = path switch
        {
            "/health" => "{}",
            "/results" => $$"""{"game_id":{{ResultsGameId}},"winners":[],"tie":false,"standings":[]}""",
            "/teams" => $$"""{"game_id":{{GameId}},"status":"{{Status}}","teams":[{"name":"apple","available":{{(!Claimed).ToString().ToLowerInvariant()}}}]}""",
            "/teams/select" => $$"""{"team":"apple","token":"{{issuedToken}}","message":"Selected"}""",
            "/me" => """{"team":"apple","authenticated":true}""",
            "/me/inventory" => """{"money":100,"fruit":0,"seeds":0,"poison":0,"molotov":0}""",
            "/me/plots" => """{"plots":[]}""",
            "/trees" => """{"trees":[]}""",
            "/market" => """{"items":[{"item":"plot","price":100}]}""",
            "/market/buy" => "{}",
            _ => null
        };
        context.Response.ContentType = "application/json";
        if (json is null) { context.Response.StatusCode = 404; json = "{}"; }
        await context.Response.WriteAsync(json);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
