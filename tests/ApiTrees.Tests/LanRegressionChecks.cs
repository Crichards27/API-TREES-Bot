using System.Text.Json;
using ApiTrees.Core;
using ApiTrees.Ui;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class LanRegressionChecks
{
    private static async Task RejectAsync(Func<Task> action, Action<bool, string> check, string message)
    {
        try { await action(); }
        catch (ConnectionException ex) when (!ex.Uncertain) { check(true, message); return; }
        check(false, message);
    }

    private static async Task<string> RenderAsync(PlayerController player)
    {
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(player).AddSingleton<ICertificatePicker, TestCertificatePicker>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Dashboard>()).ToHtmlString());
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "api-trees-lan-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var certificate = LocalGameServer.Certificate();
            await using var server = await LocalGameServer.StartAsync(certificate);
            var der = Path.Combine(directory, "server.cer");
            await File.WriteAllBytesAsync(der, certificate.RawData);
            using var client = new GameClient(server.Url, der);
            await client.GetAsync<JsonElement>("/health", false);
            check(true, "self-signed HTTPS accepts a supplied DER certificate without modifying system trust");
            var pem = Path.Combine(directory, "server.crt");
            await File.WriteAllTextAsync(pem, certificate.ExportCertificatePem());
            using (var pemClient = new GameClient(server.Url, pem))
                await pemClient.GetAsync<JsonElement>("/health", false);
            check(true, "self-signed HTTPS also accepts PEM certificates");
            using (var strict = new GameClient(server.Url))
                await RejectAsync(() => strict.GetAsync<JsonElement>("/health", false), check, "untrusted self-signed HTTPS is rejected by default");
            using (var bypass = new GameClient(server.Url, insecure: true))
                await bypass.GetAsync<JsonElement>("/health", false);
            check(true, "explicit per-connection LAN bypass works");
            try
            {
                using var invalid = new GameClient(server.Url, pem, insecure: true);
                check(false, "certificate trust and TLS bypass cannot be combined");
            }
            catch (ArgumentException) { check(true, "certificate trust and TLS bypass cannot be combined"); }
            using var other = LocalGameServer.Certificate("other.invalid");
            var wrong = Path.Combine(directory, "wrong.crt");
            await File.WriteAllTextAsync(wrong, other.ExportCertificatePem());
            using (var wrongRoot = new GameClient(server.Url, wrong))
                await RejectAsync(() => wrongRoot.GetAsync<JsonElement>("/health", false), check, "a different self-signed certificate cannot impersonate the game server");
            await using (var wrongName = await LocalGameServer.StartAsync(other))
            using (var named = new GameClient(wrongName.Url, wrong))
                await RejectAsync(() => named.GetAsync<JsonElement>("/health", false), check, "supplied certificates still require matching hostnames");
            using var root = LocalGameServer.Certificate("Game CA", authority: true);
            using var leaf = LocalGameServer.Certificate(issuer: root);
            var ca = Path.Combine(directory, "root.crt");
            await File.WriteAllTextAsync(ca, root.ExportCertificatePem());
            await using (var privateCa = await LocalGameServer.StartAsync(leaf))
            using (var trusted = new GameClient(privateCa.Url, ca))
                await trusted.GetAsync<JsonElement>("/health", false);
            check(true, "private-CA signed HTTPS works without installing the CA globally");
            using var clientOnlyCa = LocalGameServer.Certificate("Client-only CA", authority: true, serverAuthentication: false);
            using var wrongUsageLeaf = LocalGameServer.Certificate(issuer: clientOnlyCa);
            var wrongUsagePath = Path.Combine(directory, "client-only-ca.crt");
            await File.WriteAllTextAsync(wrongUsagePath, clientOnlyCa.ExportCertificatePem());
            await using (var wrongUsageServer = await LocalGameServer.StartAsync(wrongUsageLeaf))
            using (var wrongUsage = new GameClient(wrongUsageServer.Url, wrongUsagePath))
                await RejectAsync(() => wrongUsage.GetAsync<JsonElement>("/health", false), check,
                    "private trust still enforces server-authentication certificate usage");
            await using (var recoveryServer = await LocalGameServer.StartAsync(certificate))
            await using (var recovery = new PlayerController(new FailOnceStore()))
            {
                await recovery.ConnectAsync(recoveryServer.Url, pem, false);
                await recovery.ClaimAsync("apple");
                var issued = recovery.Session;
                check(recovery.NeedsSessionSave && issued is not null, "failed session save retains the issued token");
                recoveryServer.GameId++;
                await recovery.RefreshAsync();
                var recoveryHtml = await RenderAsync(recovery);
                check(!recovery.CurrentSession && recoveryHtml.Contains("Retry Save session"),
                    "session save recovery stays visible after the round changes");
                var claimWrites = recoveryServer.Writes;
                await recovery.ClaimAsync("apple");
                check(ReferenceEquals(issued, recovery.Session) && recoveryServer.Writes == claimWrites && recovery.NeedsSessionSave,
                    "another claim cannot replace an unsaved token after a round change");
                recoveryServer.NoRound = true;
                await recovery.RefreshAsync();
                check(recovery.Round is null && (await RenderAsync(recovery)).Contains("Retry Save session"),
                    "session save recovery also stays visible when there is no current round");
                await recovery.SaveSessionAsync();
                check(!recovery.NeedsSessionSave && recovery.Error is null && ReferenceEquals(issued, recovery.Session),
                    "old-round token save can be retried without claiming again");
            }
            await using (var raceServer = await LocalGameServer.StartAsync(certificate))
            {
                var raceStore = new MemoryStore();
                await using var raced = new PlayerController(raceStore);
                await raced.ConnectAsync(raceServer.Url, pem, false);
                raceServer.ReplaceRoundOnSelect = true;
                await raced.ClaimAsync("apple");
                var issued = raced.Session;
                check(issued is not null && await raceStore.LoadAsync(raceServer.Url) == issued,
                    "claim racing a replacement lobby preserves the issued token in memory and storage");
                check(!raced.CurrentSession && raced.Orchard is null &&
                    JsonSerializer.SerializeToElement(issued, GameJson.Default.Session).TryGetProperty("round_unconfirmed", out var unconfirmed) && unconfirmed.GetBoolean(),
                    "replacement between GET teams and POST select is saved as an unconfirmed round, not the old authoritative ID");
                check((await RenderAsync(raced)).Contains("Host-verified round ID") &&
                    (await RenderAsync(raced)).Contains("Confirm host-verified round"),
                    "unconfirmed claim exposes explicit host-verified recovery rather than guessing from /me");
                raceServer.ResultsGameId = issued!.GameId;
                await raced.RefreshAsync();
                check(raced.Results is null, "unconfirmed pre-claim round observation is not treated as authoritative results identity");
                raceServer.GameId++;
                await raced.RefreshAsync();
                var claimWrites = raceServer.Writes;
                await raced.ClaimAsync("apple");
                await raced.ManualAsync("buy");
                check(raceServer.Writes == claimWrites && ReferenceEquals(issued, raced.Session),
                    "an unconfirmed issued team cannot be re-claimed or used for writes after another round change");
                await using var resumed = new PlayerController(raceStore);
                raceServer.NoRound = true;
                await resumed.ConnectAsync(raceServer.Url, pem, false);
                check(resumed.Connected && !resumed.CurrentSession && resumed.Orchard is null && resumed.Session == issued && resumed.Results is null,
                    "restart retains the unconfirmed token without upgrading global /me authentication or the old observation into round proof");
                raceServer.NoRound = false;
                await resumed.RefreshAsync();
                await resumed.ClaimAsync("apple");
                check(raceServer.Writes == claimWrites, "persisted unconfirmed claims also block re-claiming");
                await resumed.ConfirmClaimRoundAsync(raceServer.GameId, false);
                check(resumed.Session == issued && resumed.Error is not null,
                    "recovery refuses a round ID without explicit host-verification attestation");
                await resumed.ConfirmClaimRoundAsync(0, true);
                check(resumed.Session == issued && resumed.Error is not null, "host-verified recovery rejects nonpositive IDs");
                using (var oldToken = new GameClient(raceServer.Url, pem) { Token = issued!.Token })
                {
                    check((await oldToken.GetAsync<Identity>("/me")).Authenticated,
                        "fixture realistically authenticates an old-round token without revealing its round");
                }
                raceStore.FailSave = true;
                await resumed.ConfirmClaimRoundAsync(raceServer.GameId - 1, true);
                check(resumed.NeedsSessionSave && resumed.NeedsRefresh && resumed.Session?.Token == issued.Token &&
                    (await RenderAsync(resumed)).Contains("Retry Save session"),
                    "failed persistence of a host-confirmed association retains the token with explicit save recovery");
                raceStore.FailSave = false;
                await resumed.SaveSessionAsync();
                check(resumed.Session is { RoundUnconfirmed: false } && resumed.Session.GameId == raceServer.GameId - 1 &&
                    resumed.Session.Token == issued!.Token && await raceStore.LoadAsync(raceServer.Url) == resumed.Session && resumed.NeedsRefresh,
                    "explicit host verification persists the same token under the attested round and requires refresh");
                await resumed.RefreshAsync();
                check(!resumed.CurrentSession && resumed.Error is null && !resumed.NeedsRefresh,
                    "host verification of an older round does not authorize the current round");
            }
            foreach (var failSave in new[] { false, true })
            {
                await using var unverifiableServer = await LocalGameServer.StartAsync(certificate);
                var unverifiableStore = new MemoryStore { FailSave = failSave };
                await using var unverifiable = new PlayerController(unverifiableStore);
                await unverifiable.ConnectAsync(unverifiableServer.Url, pem, false);
                unverifiableServer.FailTeamsAfterSelect = true;
                await unverifiable.ClaimAsync("apple");
                var issued = unverifiable.Session;
                check(issued is { RoundUnconfirmed: true } && !unverifiable.CurrentSession && unverifiable.Orchard is null &&
                    unverifiable.NeedsSessionSave == failSave,
                    $"failed post-claim round verification retains an unconfirmed token (save failure: {failSave})");
                var claimWrites = unverifiableServer.Writes;
                await unverifiable.ClaimAsync("apple");
                check(unverifiableServer.Writes == claimWrites && ReferenceEquals(issued, unverifiable.Session),
                    $"unverifiable claim cannot be replaced while recovery is pending (save failure: {failSave})");
                unverifiableStore.FailSave = false;
                if (failSave) await unverifiable.SaveSessionAsync();
                check(!unverifiable.NeedsSessionSave && await unverifiableStore.LoadAsync(unverifiableServer.Url) == issued,
                    $"unverifiable claim is durably recoverable without retrying select (save failure: {failSave})");
                unverifiableServer.FailTeamsAfterSelect = false;
                await unverifiable.RefreshAsync();
                check(!unverifiable.CurrentSession && unverifiable.Session == issued,
                    $"later matching teams read does not silently confirm an unverifiable claim (save failure: {failSave})");
                await unverifiable.ConfirmClaimRoundAsync(unverifiableServer.GameId, true);
                await unverifiable.RefreshAsync();
                check(unverifiable.CurrentSession && unverifiable.Orchard is not null && unverifiable.Error is null,
                    $"host-verified recovery restores the issued team without re-claiming (save failure: {failSave})");
            }
            await using var player = new PlayerController(new MemoryStore());
            await player.ConnectAsync(server.Url, pem, false);
            check(player.Connected && player.Round?.Status == "lobby", "GUI connects over self-signed HTTPS");
            await player.ClaimAsync("apple");
            check(player.CurrentSession && player.Error is null, "GUI claims and saves a team over HTTPS");
            var plainHtml = await RenderAsync(player);
            check(plainHtml.Contains("API Trees Bot") && plainHtml.Contains("<th>Row/column</th>") && plainHtml.Contains("<th>Branches</th>") &&
                !plainHtml.Contains("plot-id") && !plainHtml.Contains("tree-icon") && !plainHtml.Contains("Your orchard. Under control."),
                "dashboard uses a plain title and dense orchard table instead of decorative branding and tiles");
            server.Status = "active";
            var cycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Changed += () => { if (player.Messages.Any(m => m.Contains("Harvest +"))) cycle.TrySetResult(); };
            await player.StartAsync(new BotOptions(BankOnly: true));
            await cycle.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await player.StopAsync();
            check(player.Round?.Status == "active", "GUI round status follows the bot from lobby to active");
            var orchard = player.Orchard;
            server.FailHealth = true;
            await player.ConnectAsync(server.Url, pem, false);
            check(player.Error is not null && player.Connected && player.CurrentSession && ReferenceEquals(player.Orchard, orchard),
                "a failed reconnect preserves the usable connection and claimed team");
            server.FailHealth = false;
            server.FailWrite = true;
            await player.ManualAsync("buy");
            check(player.Error?.Contains("may have succeeded") == true, "GUI reports uncertain manual writes");
            var writes = server.Writes;
            await player.ManualAsync("buy");
            check(server.Writes == writes && player.Error?.Contains("Refresh") == true,
                "uncertain manual writes cannot be repeated until the orchard is refreshed");
            server.FailWrite = false;
            await player.RefreshAsync();
            await player.ManualAsync("buy");
            check(server.Writes == writes + 1 && player.Error is null, "successful refresh unlocks manual actions");
            server.FailSnapshot = true;
            var beforeSuccess = server.Writes;
            await player.ManualAsync("buy");
            check(server.Writes == beforeSuccess + 1 && player.NeedsRefresh && player.Error?.Contains("Completed buy") == true &&
                player.Error.Contains("refresh failed", StringComparison.OrdinalIgnoreCase),
                "successful manual write with a failed snapshot reports success plus refresh failure");
            await player.ManualAsync("buy");
            await player.StartAsync(new BotOptions());
            check(server.Writes == beforeSuccess + 1 && !player.Running,
                "failed post-write snapshot blocks duplicate manual actions and bot restart");
            await player.RefreshAsync();
            check(player.NeedsRefresh, "failed explicit refresh does not unlock a completed write");
            server.FailSnapshot = false;
            await player.RefreshAsync();
            await player.ManualAsync("buy");
            check(!player.NeedsRefresh && player.Error is null && server.Writes == beforeSuccess + 2,
                "successful explicit refresh unlocks actions after a completed write's snapshot failure");
            server.NoRound = true;
            await using var idle = new PlayerController(new MemoryStore());
            await idle.ConnectAsync(server.Url, pem, false);
            check(idle.Connected && idle.Round is null && idle.Results is null && idle.Error is null,
                "GUI can connect before the host creates its first round");
            await using var services = new ServiceCollection().AddLogging()
                .AddSingleton(idle).AddSingleton<ICertificatePicker, TestCertificatePicker>().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Dashboard>()).ToHtmlString());
            check(html.Contains("Waiting for round"), "idle dashboard does not mislabel a game as ended");
            check(html.Contains("No orchard loaded. Connect and claim a team.") && !html.Contains("empty-state"),
                "dashboard empty state is plain functional text");
        }
        finally { Directory.Delete(directory, true); }
    }
}

internal sealed class TestCertificatePicker : ICertificatePicker
{
    public Task<string?> PickAsync() => Task.FromResult<string?>(null);
}
