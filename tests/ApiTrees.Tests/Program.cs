using ApiTrees.Core;
using ApiTrees.Ui;
using System.Net;
using System.Text.Json;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS " + message);
    passed++;
}
async Task Throws<T>(Func<Task> action, Func<T, bool> check, string message) where T : Exception
{
    try { await action(); }
    catch (T ex) { Check(check(ex), message); return; }
    throw new Exception("FAILED (no exception): " + message);
}

// Client safety and compatibility regressions, without a network dependency.
Check(GameClient.NormalizeServer("HTTPS://example.test/") == "https://example.test", "URL normalization");
await Throws<ArgumentException>(() => Task.FromResult(GameClient.NormalizeServer("https://secret@example.test")), _ => true, "URLs cannot embed credentials");
var records = new List<(string Method, string Path, string? Auth, string? Body)>();
var handler = new FakeHandler(async request =>
{
    records.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
        request.Content is null ? null : await request.Content.ReadAsStringAsync()));
    return FakeHandler.Json("{\"team\":\"apple\",\"money\":300,\"plots\":3,\"trees\":1,\"plot_ids\":[1,2,3]}");
});
using (var client = new GameClient("https://example.test", handler) { Token = "private-token" })
{
    var team = await client.GetAsync<PublicTeam>("/team/apple", false);
    Check(team.PlotIds.SequenceEqual([1, 2, 3]), "latest public-team plot_ids deserialize");
    Check(records[^1].Auth is null, "public requests never carry team tokens");
    await client.ActAsync(HttpMethod.Patch, "/inventory/fruit", new ConvertRequest(2, "seeds"));
    Check(records[^1].Auth == "Bearer private-token" && records[^1].Body == "{\"amount\":2,\"convert_to\":\"seeds\"}", "authenticated snake_case action payload");
}
using (var client = new GameClient("https://example.test", new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
    { Headers = { Location = new Uri("https://other.test") }, Content = new StringContent("") }))) { Token = "private-token" })
    await Throws<ApiException>(() => client.GetAsync<Inventory>("/me/inventory"), e => e.StatusCode == 302, "redirect responses are rejected");

foreach (var response in new[] { FakeHandler.Json("{\"detail\":\"server failed\"}", HttpStatusCode.InternalServerError), FakeHandler.Json("not json") })
{
    var calls = 0;
    using var client = new GameClient("https://example.test", new FakeHandler(_ => { calls++; return Task.FromResult(response); })) { Token = "private-token" };
    await Throws<ConnectionException>(() => client.ActAsync(HttpMethod.Put, "/plots/1/tree"), e => e.Uncertain, "failed/non-JSON writes are marked uncertain");
    Check(calls == 1, "uncertain writes are never retried");
}
using (var client = new GameClient("https://example.test", new FakeHandler(_ => throw new HttpRequestException("lost connection"))) { Token = "token" })
{
    await Throws<ConnectionException>(() => client.GetAsync<Inventory>("/me/inventory"), e => !e.Uncertain, "failed reads may refresh safely");
    await Throws<ConnectionException>(() => client.ActAsync(HttpMethod.Delete, "/trees/1"), e => e.Uncertain, "lost write response is uncertain");
}

var cancelledHandler = new CancelledWriteHandler();
using (var client = new GameClient("https://example.test", cancelledHandler) { Token = "token" })
using (var cancellation = new CancellationTokenSource())
{
    var write = client.ActAsync(HttpMethod.Post, "/market/buy", ct: cancellation.Token);
    await cancelledHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    await Throws<ConnectionException>(() => write,
        e => e.Uncertain, "cancelling an in-flight write still reports an uncertain outcome");
}
using (var client = new GameClient("https://example.test", new FakeHandler(_ => throw new Exception("Unexpected network request"))) { Token = "token" })
    await Throws<OperationCanceledException>(() => client.ActAsync(HttpMethod.Post, "/market/buy", ct: new CancellationToken(true)),
        _ => true, "already-cancelled writes stop before reaching the network");

Check(SessionKeys.ForServer("HTTPS://example.test/") == SessionKeys.ForServer("https://example.test"),
    "secure session keys normalize equivalent server addresses");
Check(SessionKeys.ForServer("https://example.test") != SessionKeys.ForServer("https://another.test"),
    "secure session keys isolate game servers");

var legacySession = JsonSerializer.Deserialize("""{"server":"https://example.test","team":"apple","token":"token","game_id":7}""", GameJson.Default.Session)!;
var (legacyServer, legacyTeam, legacyToken, legacyGameId) = legacySession;
Check(!legacySession.RoundUnconfirmed && legacyGameId == 7 && legacyServer == "https://example.test" && legacyTeam == "apple" && legacyToken == "token",
    "legacy session JSON and four-field construction/deconstruction remain compatible");
var unconfirmedSession = legacySession with { RoundUnconfirmed = true };
Check(JsonSerializer.Deserialize(JsonSerializer.Serialize(unconfirmedSession, GameJson.Default.Session), GameJson.Default.Session) == unconfirmedSession,
    "unconfirmed round marker survives source-generated session serialization");

var seen = new List<string>();
using (var client = new GameClient("https://example.test", new FakeHandler(request =>
{
    var path = request.RequestUri!.AbsolutePath;
    seen.Add(request.Method + " " + path);
    return Task.FromResult(path == "/teams" ? FakeHandler.Json("{\"detail\":\"No game exists.\"}", HttpStatusCode.NotFound) :
        FakeHandler.Json("{\"game_id\":7,\"winners\":[\"apple\"],\"tie\":false,\"standings\":[]}"));
})) { Token = "token" })
{
    var engine = new BotEngine(client);
    Results? finished = null;
    engine.Finished += r => finished = r;
    await engine.RunAsync(new Session(client.Server, "apple", "token", 7), new BotOptions(), CancellationToken.None);
    Check(finished?.GameId == 7 && seen.SequenceEqual(["GET /teams", "GET /results"]), "404 from /teams resolves matching results without writes");
}
seen.Clear();
using (var client = new GameClient("https://example.test", new FakeHandler(request =>
{
    seen.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
    return Task.FromResult(request.RequestUri.AbsolutePath == "/teams" ?
        FakeHandler.Json("{\"game_id\":8,\"status\":\"lobby\",\"teams\":[]}") :
        FakeHandler.Json("{\"game_id\":6,\"winners\":[],\"tie\":false,\"standings\":[]}"));
})) { Token = "token" })
{
    var engine = new BotEngine(client);
    var wrongResultsShown = false;
    engine.Finished += _ => wrongResultsShown = true;
    await engine.RunAsync(new Session(client.Server, "apple", "token", 7), new BotOptions(), CancellationToken.None);
    Check(!wrongResultsShown && seen.All(r => r.StartsWith("GET")), "new round stops old token and does not mislabel another round's results");
}

// A stateful orchard emulator checks spending and defense order rather than mirroring methods.
var orchard = new OrchardHandler();
using (var client = new GameClient("https://example.test", orchard) { Token = "token" })
{
    var engine = new BotEngine(client);
    Check(await engine.HarvestAndDefendAsync() == 10, "healthy fruit harvested while burning/dead fruit is ignored");
    Check(orchard.Writes.Take(2).SequenceEqual(["DELETE /branches/2", "PATCH /branches/1"]), "poison defense precedes harvesting");
    await engine.GrowAsync(new BotOptions(MaxTrees: 4, Reserve: 100));
    Check(orchard.PlotCount == 4 && orchard.PlantedPlots.Count == 3, "fills empty land then expands up to tree cap");
    Check(orchard.Money >= 100 && orchard.Purchases.All(q => q == 1), "money reserve respected and plots purchased singly");
    Check(orchard.Writes.All(w => !w.StartsWith("DELETE /trees/")), "burning trees are not prematurely deleted");
}

if (args is ["--integration", var server])
{
    using var client = new GameClient(server);
    using var admin = new GameClient(server) { Token = "integration-only" };
    await admin.ActAsync(HttpMethod.Post, "/_test/reset");
    var lobby = await client.GetAsync<TeamList>("/teams", false);
    Check(lobby.Status == "lobby" && lobby.Teams.Count == 8, "upstream lobby/team list");
    var store = new MemoryStore();
    var session = await TeamSessions.ClaimAsync(client, lobby, "apple", store);
    var opponent = await client.CallAsync<TeamSelection>(HttpMethod.Post, "/teams/select", new SelectRequest("orange"), false);
    var engine = new BotEngine(client);
    var initial = await engine.SnapshotAsync();
    Check(initial.Plots.Select(p => (p.Row, p.Column)).SequenceEqual([(0, 0), (0, 1), (0, 2)]) && initial.Trees.Count == 1, "upstream initial grid and tree");
    await admin.ActAsync(HttpMethod.Post, "/admin/start");
    await admin.ActAsync(HttpMethod.Post, "/_test/seed");
    Check(await engine.HarvestAndDefendAsync() == 10, "upstream harvest and poison pruning");
    await engine.GrowAsync(new BotOptions(MaxTrees: 4, Reserve: 100));
    var grown = await engine.SnapshotAsync();
    Check(grown.Trees.Count == 4 && grown.Plots.Count == 4 && grown.Inventory.Money >= 100, "upstream seed conversion, planting and single-plot expansion");
    var publicTeam = await client.GetAsync<PublicTeam>("/team/orange", false);
    Check(publicTeam.PlotIds.Count == 3 && publicTeam.Trees == 1, "upstream latest opponent plot IDs");
    var poison = await client.ActAsync(HttpMethod.Post, "/market/buy", new BuyRequest("poison", 1));
    Check(poison.GetProperty("poison").GetInt32() == 1, "upstream current market response fields");
    using var opponentClient = new GameClient(server) { Token = opponent.Token };
    var enemyTree = (await opponentClient.GetAsync<TreeList>("/trees")).Trees.Single();
    await client.ActAsync(HttpMethod.Post, "/attacks/poison", new AttackRequest(enemyTree.PlotId));
    Check((await opponentClient.GetAsync<TreeList>("/trees")).Trees.Single().Branches.Any(b => b.Status == "poisoned"), "upstream manual poison attack");
    var beforeBank = await engine.SnapshotAsync();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    engine.Updated += _ => stop.Cancel();
    try { await engine.RunAsync(session, new BotOptions(BankOnly: true), stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    var afterBank = await engine.SnapshotAsync();
    Check(beforeBank.Plots.Count == afterBank.Plots.Count && beforeBank.Inventory.Money == afterBank.Inventory.Money, "upstream bank-only cycle does not invest");
    await admin.ActAsync(HttpMethod.Post, "/_test/kill-tree");
    await engine.GrowAsync(new BotOptions(MaxTrees: 4, Reserve: 100));
    Check((await engine.SnapshotAsync()).Trees.All(t => t.Status != "dead"), "upstream dead-tree replacement");
    await admin.ActAsync(HttpMethod.Post, "/admin/end");
    Results? final = null;
    engine.Finished += r => final = r;
    await engine.RunAsync(session, new BotOptions(), CancellationToken.None);
    Check(final?.GameId == session.GameId && final.Standings.Count == 2, "upstream finished round returns results after /teams 404");
    var mine = final!.Standings.Single(t => t.Team == "apple");
    Check(mine.Score == (await engine.SnapshotAsync()).EstimatedScore, "upstream final scoring equals dashboard estimate");
    await admin.ActAsync(HttpMethod.Post, "/_test/new-round");
    await engine.RunAsync(session, new BotOptions(), CancellationToken.None);
    Check((await client.GetAsync<TeamList>("/teams", false)).GameId != session.GameId, "upstream new round does not reuse old session");
    await using (var player = new PlayerController(store))
    {
        await player.ConnectAsync(server, null, false);
        Check(!player.CurrentSession && player.Orchard is null, "GUI rejects an old-round saved session");
        await player.ClaimAsync("apple");
        Check(player.CurrentSession && player.Orchard?.Plots.Count == 3, "GUI claims and refreshes the new round");
        await player.ManualAsync("buy");
        Check(player.Error?.Contains("must be active") == true, "GUI manual controls enforce active round");
        await player.StartAsync(new BotOptions());
        await player.StopAsync();
        Check(!player.Running && player.CurrentSession, "GUI stop in lobby retains session");
    }
    await using (var player = new PlayerController(new FailOnceStore()))
    {
        await player.ConnectAsync(server, null, false);
        await player.ClaimAsync("orange");
        Check(player.NeedsSessionSave && player.CurrentSession, "GUI retains an issued token if secure saving fails");
        await player.ConnectAsync("http://another.test", null, false);
        Check(player.NeedsSessionSave && player.Connected && player.CurrentSession, "GUI cannot discard an unsaved claimed token on reconnect");
        await player.SaveSessionAsync();
        Check(!player.NeedsSessionSave && player.Error is null, "GUI can retry session persistence without claiming twice");
    }
}
await LanRegressionChecks.RunAsync(Check);
await TlsCertificateLifetimeChecks.RunAsync(Check);
Console.WriteLine($"All {passed} checks passed.");

sealed class CancelledWriteHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The write should be cancelled.");
    }
}

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };
}
sealed class MemoryStore : ISessionStore
{
    private Session? session;
    public Task<Session?> LoadAsync(string server) => Task.FromResult(session?.Server == server ? session : null);
    public Task SaveAsync(Session value)
    {
        if (FailSave) throw new IOException("Storage temporarily unavailable.");
        session = value;
        return Task.CompletedTask;
    }
    public bool FailSave { get; set; }
}
sealed class FailOnceStore : ISessionStore
{
    private bool first = true;
    public Task<Session?> LoadAsync(string server) => Task.FromResult<Session?>(null);
    public Task SaveAsync(Session session)
    {
        if (first) { first = false; throw new IOException("Storage temporarily unavailable."); }
        return Task.CompletedTask;
    }
}
sealed class OrchardHandler : HttpMessageHandler
{
    private int fruit, seeds = 3;
    public int Money { get; private set; } = 300;
    public int PlotCount { get; private set; } = 3;
    public List<int> PlantedPlots { get; } = [];
    public List<int> Purchases { get; } = [];
    public List<string> Writes { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get)
        {
            if (path == "/me/inventory") return FakeHandler.Json($"{{\"money\":{Money},\"fruit\":{fruit},\"seeds\":{seeds},\"poison\":0,\"molotov\":0}}");
            if (path == "/me/plots") return FakeHandler.Json("{\"plots\":[" + string.Join(",", Enumerable.Range(1, PlotCount).Select(i => $"{{\"id\":{i},\"row\":{(i - 1) / 4},\"column\":{(i - 1) % 4},\"tree_id\":{(i == 1 || PlantedPlots.Contains(i) ? i.ToString() : "null")},\"connected_plots\":[]}}")) + "]}");
            if (path == "/trees") return FakeHandler.Json("""
                {"trees":[{"id":1,"plot_id":1,"status":"healthy","branch_count":2,"fruit":10,"branches":[{"id":1,"status":"healthy","fruit":10},{"id":2,"status":"poisoned","fruit":0}]},
                {"id":90,"plot_id":90,"status":"burning","branch_count":1,"fruit":8,"branches":[{"id":91,"status":"healthy","fruit":8}]},
                {"id":92,"plot_id":92,"status":"healthy","branch_count":1,"fruit":0,"branches":[{"id":93,"status":"dead","fruit":0}]}]}
                """);
            if (path == "/market") return FakeHandler.Json("{\"items\":[{\"item\":\"plot\",\"price\":100}]}");
        }
        Writes.Add(request.Method + " " + path);
        using var body = JsonDocument.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct));
        if (path == "/branches/1") { fruit += 10; return FakeHandler.Json("{\"harvested\":10}"); }
        if (path == "/inventory/fruit")
        {
            var amount = body.RootElement.GetProperty("amount").GetInt32();
            fruit -= amount;
            if (body.RootElement.GetProperty("convert_to").GetString() == "seeds") seeds += amount * 5;
            else Money += amount * 10;
        }
        if (path.EndsWith("/tree")) { PlantedPlots.Add(int.Parse(path.Split('/')[2])); seeds -= 3; }
        if (path == "/market/buy")
        {
            Purchases.Add(body.RootElement.GetProperty("quantity").GetInt32());
            Money -= 100;
            PlotCount++;
            return FakeHandler.Json($"{{\"plot_ids\":[{PlotCount}]}}");
        }
        return FakeHandler.Json("{}");
    }
}
