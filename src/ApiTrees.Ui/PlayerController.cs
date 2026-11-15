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
    public bool NeedsRefresh { get; private set; }
    public bool Connected => client is not null;
    public string? Error { get; private set; }
    public string[] Messages => messages.ToArray();
    public bool CurrentSession => Session is { RoundUnconfirmed: false } && Session.GameId == Round?.GameId;

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
        catch (Exception ex)
        {
            if (ex is ConnectionException { Uncertain: true }) NeedsRefresh = true;
            Error = ex.Message;
            Log(ex.Message);
        }
        finally { Busy = false; gate.Release(); Changed?.Invoke(); }
    }

    public Task ConnectAsync(string server, string? caPath, bool insecure) => ExecuteAsync(async () =>
    {
        if (NeedsSessionSave) throw new InvalidOperationException("Save your claimed team session before changing the connection.");
        var next = new GameClient(server, string.IsNullOrWhiteSpace(caPath) ? null : caPath, insecure);
        try
        {
            // Stage the complete connection before replacing a usable session.
            await next.GetAsync<System.Text.Json.JsonElement>("/health", false);
            var saved = await sessions.LoadAsync(next.Server);
            TeamList? round = null;
            Results? finished = null;
            try { round = await next.GetAsync<TeamList>("/teams", false); }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                try { finished = await next.GetAsync<Results>("/results", false); }
                catch (ApiException resultError) when (resultError.StatusCode == 404) { }
                if (saved is not null && (saved.RoundUnconfirmed || finished?.GameId != saved.GameId)) finished = null;
            }
            var current = saved is { RoundUnconfirmed: false } && saved.GameId == round?.GameId;
            if (current)
            {
                next.Token = saved!.Token;
                var me = await next.GetAsync<Identity>("/me");
                if (!me.Authenticated || me.Team != saved.Team) throw new InvalidOperationException("Saved team does not match the server.");
            }
            var nextEngine = new BotEngine(next);
            var orchard = current ? await nextEngine.SnapshotAsync() : null;
            var opponents = await LoadOpponentsAsync(next, round, saved?.Team);
            nextEngine.Log += Log;
            nextEngine.RoundChanged += updatedRound =>
            {
                Round = updatedRound;
                if (!CurrentSession) { Orchard = null; Opponents = []; }
                Changed?.Invoke();
            };
            nextEngine.Updated += snapshot => { Orchard = snapshot; Changed?.Invoke(); };
            nextEngine.Finished += results => { Results = results; Changed?.Invoke(); };
            client?.Dispose();
            client = next;
            engine = nextEngine;
            Session = saved; Round = round; Results = finished; Orchard = orchard; Opponents = opponents;
            NeedsRefresh = false;
            Log(CurrentSession ? $"Resumed {Session!.Team}, round {Session.GameId}." : "Connected. Choose a team in the lobby.");
            if (insecure) Log("TLS verification is disabled for this connection.");
            if (next.Server.StartsWith("http://")) Log("HTTP sends team tokens without encryption. Prefer HTTPS.");
        }
        catch { if (!ReferenceEquals(client, next)) next.Dispose(); throw; }
    });

    public Task ClaimAsync(string name) => ExecuteAsync(async () =>
    {
        if (NeedsSessionSave) throw new InvalidOperationException("Save your claimed team session before claiming another team.");
        if (Session?.RoundUnconfirmed == true) throw new InvalidOperationException("A team token was already issued. Resolve its unconfirmed round with the host; do not claim again.");
        RequireFreshState();
        if (client is null || engine is null) throw new InvalidOperationException("Connect first.");
        Round = await client.GetAsync<TeamList>("/teams", false);
        if (CurrentSession) throw new InvalidOperationException("Your saved team already belongs to this round.");
        try { Session = await TeamSessions.ClaimAsync(client, Round, name, sessions); }
        catch (SessionSaveException ex)
        {
            Session = ex.Session;
            NeedsSessionSave = true;
            Orchard = null;
            throw;
        }
        Orchard = null;
        if (Session.RoundUnconfirmed)
        {
            Log($"Claimed {Session.Team}. Token saved privately; round unconfirmed. Ask the host to verify the claim's round before recovery; do not claim again.");
            return;
        }
        Round = await client.GetAsync<TeamList>("/teams", false);
        if (CurrentSession) Orchard = await engine.SnapshotAsync();
        Log($"Claimed {Session.Team}. Token saved privately.");
    });

    public Task SaveSessionAsync() => ExecuteAsync(async () =>
    {
        if (Session is null) throw new InvalidOperationException("No claimed session to save.");
        await sessions.SaveAsync(Session);
        NeedsSessionSave = false;
        Log("Team session saved privately.");
    });

    public Task ConfirmClaimRoundAsync(int gameId, bool hostVerified) => ExecuteAsync(async () =>
    {
        if (client is null || Session is not { RoundUnconfirmed: true }) throw new InvalidOperationException("No unconfirmed claim to recover.");
        if (!hostVerified || gameId < 1) throw new InvalidOperationException("Ask the host to verify the claim's round and explicitly confirm a positive round ID.");
        if (NeedsSessionSave) throw new InvalidOperationException("Save the issued token before confirming its round.");
        client.Token = Session.Token;
        var me = await client.GetAsync<Identity>("/me");
        if (!me.Authenticated || me.Team != Session.Team) throw new InvalidOperationException("Claimed team does not match the server.");
        // /me verifies the token/team ONLY. Round association is the user's
        // explicit host-verified attestation, never inferred from authentication.
        Session = Session with { GameId = gameId, RoundUnconfirmed = false };
        Orchard = null;
        Results = null;
        NeedsRefresh = true;
        NeedsSessionSave = true;
        await sessions.SaveAsync(Session);
        NeedsSessionSave = false;
        Log($"Claim round {gameId} recorded from explicit host verification. Refresh before continuing.");
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
            if (Session is null || !Session.RoundUnconfirmed && results.GameId == Session.GameId) Results = results;
        }
        catch (ApiException ex) when (ex.StatusCode == 404) { }
        await RefreshOpponentsAsync();
        NeedsRefresh = false;
    });

    private async Task RefreshOpponentsAsync() =>
        Opponents = client is null ? [] : await LoadOpponentsAsync(client, Round, Session?.Team);

    private static async Task<List<PublicTeam>> LoadOpponentsAsync(GameClient source, TeamList? round, string? ownTeam)
    {
        var opponents = new List<PublicTeam>();
        if (round is not null)
            foreach (var team in round.Teams.Where(t => !t.Available && t.Name != ownTeam))
            {
                try { opponents.Add(await source.GetAsync<PublicTeam>("/team/" + Uri.EscapeDataString(team.Name), false)); }
                catch (ApiException ex) when (ex.StatusCode == 404) { }
            }
        return opponents;
    }

    public Task StartAsync(BotOptions options) => ExecuteAsync(() =>
    {
        RequireFreshState();
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
            catch (Exception ex)
            {
                if (ex is ConnectionException { Uncertain: true }) NeedsRefresh = true;
                Error = ex.Message;
                Log("Bot stopped: " + ex.Message);
            }
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
        RequireFreshState();
        if (client is null || engine is null || Session is null) throw new InvalidOperationException("Claim a team first.");
        if (Session.RoundUnconfirmed) throw new InvalidOperationException("Resolve the claimed token's unconfirmed round with the host before any action.");
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
        NeedsRefresh = true;
        try { Orchard = await engine.SnapshotAsync(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Completed {action}, but orchard refresh failed. Refresh and inspect the orchard before another action. {ex.Message}", ex);
        }
        NeedsRefresh = false;
        Log($"Completed {action}. Orchard refreshed.");
    });

    private void RequireFreshState()
    {
        if (NeedsRefresh) throw new InvalidOperationException("Refresh the orchard before another action; the previous write may have succeeded.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        client?.Dispose();
        runningCancellation?.Dispose();
        gate.Dispose();
    }
}
