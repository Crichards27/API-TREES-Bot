using System.Collections.Concurrent;
using ApiTrees.Core;

namespace ApiTrees.Ui;

public interface ICertificatePicker
{
    Task<string?> PickAsync();
}

public sealed class PlayerController(ISessionStore sessions) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentQueue<string> messages = new();
    private GameClient? client;
    private BotEngine? engine;
    private CancellationTokenSource? runningCancellation;
    private Task? runningTask;
    public event Action? Changed;
    public TeamList? Round { get; private set; }
    public Session? Session { get; private set; }
    public OrchardSnapshot? Orchard { get; private set; }
    public Results? Results { get; private set; }
    public List<PublicTeam> Opponents { get; private set; } = [];
    public bool Running { get; private set; }
    public bool Busy { get; private set; }
    public bool NeedsSessionSave { get; private set; }
    public bool Connected => client is not null;
    public string? Error { get; private set; }
    public string[] Messages => messages.ToArray();
    public bool CurrentSession => Session is not null && Session.GameId == Round?.GameId;

    private void Log(string message)
    {
        messages.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (messages.Count > 200) messages.TryDequeue(out _);
        Changed?.Invoke();
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        await gate.WaitAsync();
        try
        {
            if (Running) throw new InvalidOperationException("Stop the bot before changing the connection or issuing manual actions.");
            Busy = true;
            Error = null;
            Changed?.Invoke();
            await action();
        }
        catch (Exception ex) { Error = ex.Message; Log(ex.Message); }
        finally { Busy = false; gate.Release(); Changed?.Invoke(); }
    }

    public Task ConnectAsync(string server, string? caPath, bool insecure) => ExecuteAsync(async () =>
    {
        if (NeedsSessionSave) throw new InvalidOperationException("Save your claimed team session before changing the connection.");
        client?.Dispose();
        client = null;
        engine = null;
        Round = null; Session = null; Orchard = null; Results = null; Opponents = [];
        var next = new GameClient(server, string.IsNullOrWhiteSpace(caPath) ? null : caPath, insecure);
        try
        {
            await next.GetAsync<System.Text.Json.JsonElement>("/health", false);
            Session = await sessions.LoadAsync(next.Server);
            try { Round = await next.GetAsync<TeamList>("/teams", false); }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                var results = await next.GetAsync<Results>("/results", false);
                if (Session is null || results.GameId == Session.GameId) Results = results;
            }
            if (CurrentSession)
            {
                next.Token = Session!.Token;
                var me = await next.GetAsync<Identity>("/me");
                if (!me.Authenticated || me.Team != Session.Team) throw new InvalidOperationException("Saved team does not match the server.");
            }
            client = next;
            engine = new BotEngine(client);
            engine.Log += Log;
            engine.Updated += snapshot => { Orchard = snapshot; Changed?.Invoke(); };
            engine.Finished += results => { Results = results; Round = null; Changed?.Invoke(); };
            if (CurrentSession) Orchard = await engine.SnapshotAsync();
            await RefreshOpponentsAsync();
            Log(CurrentSession ? $"Resumed {Session!.Team}, round {Session.GameId}." : "Connected. Choose a team in the lobby.");
            if (insecure) Log("TLS verification is disabled for this connection.");
            if (next.Server.StartsWith("http://")) Log("HTTP sends team tokens without encryption. Prefer HTTPS.");
        }
        catch { next.Dispose(); client = null; engine = null; throw; }
    });

    public Task ClaimAsync(string name) => ExecuteAsync(async () =>
    {
        if (client is null || engine is null) throw new InvalidOperationException("Connect first.");
        Round = await client.GetAsync<TeamList>("/teams", false);
        if (CurrentSession) throw new InvalidOperationException("Your saved team already belongs to this round.");
        try { Session = await TeamSessions.ClaimAsync(client, Round, name, sessions); }
        catch (SessionSaveException ex)
        {
            Session = ex.Session;
            NeedsSessionSave = true;
            Orchard = await engine.SnapshotAsync();
            throw;
        }
        Round = await client.GetAsync<TeamList>("/teams", false);
        Orchard = await engine.SnapshotAsync();
        Log($"Claimed {Session.Team}. Token saved privately.");
    });

    public Task SaveSessionAsync() => ExecuteAsync(async () =>
    {
        if (Session is null) throw new InvalidOperationException("No claimed session to save.");
        await sessions.SaveAsync(Session);
        NeedsSessionSave = false;
        Log("Team session saved privately.");
    });

    public Task RefreshAsync() => ExecuteAsync(async () =>
    {
        if (client is null) throw new InvalidOperationException("Connect first.");
        try { Round = await client.GetAsync<TeamList>("/teams", false); }
        catch (ApiException ex) when (ex.StatusCode == 404) { Round = null; }
        if (CurrentSession && engine is not null) Orchard = await engine.SnapshotAsync();
        else Orchard = null;
        try
        {
            var results = await client.GetAsync<Results>("/results", false);
            if (Session is null || results.GameId == Session.GameId) Results = results;
        }
        catch (ApiException ex) when (ex.StatusCode == 404) { }
        await RefreshOpponentsAsync();
    });

    private async Task RefreshOpponentsAsync()
    {
        var opponents = new List<PublicTeam>();
        if (client is not null && Round is not null)
            foreach (var team in Round.Teams.Where(t => !t.Available && t.Name != Session?.Team))
            {
                try { opponents.Add(await client.GetAsync<PublicTeam>("/team/" + Uri.EscapeDataString(team.Name), false)); }
                catch (ApiException ex) when (ex.StatusCode == 404) { }
            }
        Opponents = opponents;
    }

    public Task StartAsync(BotOptions options) => ExecuteAsync(() =>
    {
        options.Validate();
        if (!CurrentSession || engine is null || Session is null) throw new InvalidOperationException("Claim or resume a current team first.");
        runningCancellation?.Dispose();
        runningCancellation = new CancellationTokenSource();
        var token = runningCancellation.Token;
        Running = true;
        Log("Bot started. Keep the app open; Stop preserves your team session.");
        runningTask = Task.Run(async () =>
        {
            try { await engine.RunAsync(Session, options, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Log("Bot stopped. Session retained."); }
            catch (Exception ex) { Error = ex.Message; Log("Bot stopped: " + ex.Message); }
            finally { Running = false; Changed?.Invoke(); }
        });
        return Task.CompletedTask;
    });

    public async Task StopAsync()
    {
        runningCancellation?.Cancel();
        if (runningTask is not null) await runningTask;
    }

    public Task ManualAsync(string action, int id = 0, int amount = 1, string item = "plot") => ExecuteAsync(async () =>
    {
        if (client is null || engine is null || Session is null) throw new InvalidOperationException("Claim a team first.");
        var round = await client.GetAsync<TeamList>("/teams", false);
        if (round.GameId != Session.GameId || round.Status != "active") throw new InvalidOperationException("Your team's round must be active.");
        if (amount < 1 || action is "plant" or "prune" or "delete" or "poison" or "molotov" && id < 1)
            throw new ArgumentException("Use positive amounts and IDs.");
        switch (action)
        {
            case "harvest": await engine.HarvestAndDefendAsync(); break;
            case "plant": await client.ActAsync(HttpMethod.Put, $"/plots/{id}/tree"); break;
            case "prune": await client.ActAsync(HttpMethod.Delete, $"/branches/{id}"); break;
            case "delete": await client.ActAsync(HttpMethod.Delete, $"/trees/{id}"); break;
            case "convert":
                if (item is not ("money" or "seeds")) throw new ArgumentException("Convert to money or seeds.");
                await client.ActAsync(HttpMethod.Patch, "/inventory/fruit", new ConvertRequest(amount, item)); break;
            case "buy":
                if (item is not ("plot" or "poison" or "molotov") || item == "plot" && amount != 1)
                    throw new ArgumentException("Buy plot, poison or molotov. Plot quantity must be 1.");
                await client.ActAsync(HttpMethod.Post, "/market/buy", new BuyRequest(item, amount)); break;
            case "poison": case "molotov":
                if (Orchard?.Plots.Any(p => p.Id == id) == true) throw new ArgumentException("Select an opponent plot, not your own.");
                await client.ActAsync(HttpMethod.Post, "/attacks/" + action, new AttackRequest(id)); break;
            default: throw new ArgumentException("Unknown action.");
        }
        Orchard = await engine.SnapshotAsync();
        Log($"Completed {action}. Orchard refreshed.");
    });

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        client?.Dispose();
        runningCancellation?.Dispose();
        gate.Dispose();
    }
}
