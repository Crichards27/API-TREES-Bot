using System.Diagnostics;
using System.Text.Json;

namespace ApiTrees.Core;

public sealed record BotOptions(int IntervalSeconds = 2, int MaxTrees = 0, int Reserve = 0,
    int ExpansionBatch = 4, int? RoundSeconds = null, bool BankOnly = false)
{
    public void Validate()
    {
        if (IntervalSeconds < 1 || MaxTrees < 0 || Reserve < 0 || ExpansionBatch < 1 || RoundSeconds is <= 0)
            throw new ArgumentException("Interval/batch/duration must be positive; tree cap and reserve must be nonnegative.");
    }
}

public sealed record OrchardSnapshot(Inventory Inventory, List<Plot> Plots, List<Tree> Trees, int Harvested = 0)
{
    // The game's final scorer counts every extant tree, including dead trees.
    public long EstimatedScore => (long)Inventory.Money + 10L * Inventory.Fruit + 100L * Trees.Count;
}

public sealed class BotEngine(GameClient client)
{
    public event Action<string>? Log;
    public event Action<OrchardSnapshot>? Updated;
    public event Action<Results>? Finished;
    public event Action<TeamList?>? RoundChanged;

    public async Task<OrchardSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var inventory = await client.GetAsync<Inventory>("/me/inventory", ct: ct);
        var plots = await client.GetAsync<PlotList>("/me/plots", ct: ct);
        var trees = await client.GetAsync<TreeList>("/trees", ct: ct);
        return new(inventory, plots.Plots, trees.Trees);
    }

    public async Task RunAsync(Session session, BotOptions options, CancellationToken ct)
    {
        options.Validate();
        if (session.Server != client.Server || session.Token != client.Token) throw new InvalidOperationException("Session does not match this connection.");
        Stopwatch? activeTime = null;
        var failures = 0;
        string? previousStatus = null;
        var wasBanking = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var round = await CurrentRoundAsync(session.GameId, ct);
                if (round is null) return;
                if (round.Status != previousStatus)
                {
                    Log?.Invoke($"Round {round.GameId}: {round.Status}" + (round.Status == "lobby" ? "; waiting for the host." : ""));
                    previousStatus = round.Status;
                }
                if (round.Status == "active")
                {
                    activeTime ??= Stopwatch.StartNew();
                    var banking = options.BankOnly || options.RoundSeconds is int duration && duration - activeTime.Elapsed.TotalSeconds <= 45;
                    if (banking && !wasBanking) Log?.Invoke("Banking points: harvesting and defending; investments stopped.");
                    wasBanking = banking;
                    var harvested = await HarvestAndDefendAsync(ct);
                    if (!banking) await GrowAsync(options, ct);
                    var snapshot = (await SnapshotAsync(ct)) with { Harvested = harvested };
                    Updated?.Invoke(snapshot);
                    Log?.Invoke($"Harvest +{harvested} | fruit {snapshot.Inventory.Fruit} | trees {snapshot.Trees.Count} | estimated score {snapshot.EstimatedScore}");
                }
                failures = 0;
            }
            catch (ApiException ex) when (ex.StatusCode == 409 && ex.Detail.Contains("only allowed while the game is active"))
            {
                // Game ended between a status read and an action. Never retry that write.
                if (await CurrentRoundAsync(session.GameId, ct) is null) return;
                throw new InvalidOperationException("This saved team's round is not active. Reconnect and claim in the next lobby.", ex);
            }
            catch (ConnectionException ex) when (!ex.Uncertain)
            {
                if (++failures >= 5) throw;
                Log?.Invoke($"Read failed; refreshing state ({failures}/5): {ex.Message}");
            }
            catch (ApiException ex) when (ex.StatusCode >= 500 || ex.StatusCode == 429)
            {
                if (++failures >= 5) throw;
                Log?.Invoke($"Server busy; refreshing state ({failures}/5): {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds((long)options.IntervalSeconds * (failures == 0 ? 1 : Math.Min(4, 1 << failures))), ct);
        }
    }

    private async Task<TeamList?> CurrentRoundAsync(int gameId, CancellationToken ct)
    {
        TeamList? round;
        try { round = await client.GetAsync<TeamList>("/teams", false, ct); }
        catch (ApiException ex) when (ex.StatusCode == 404) { round = null; }
        RoundChanged?.Invoke(round);
        if (round?.GameId == gameId && round.Status is "active" or "lobby") return round;
        // /teams returns 404 once the host ends the game, not status=finished.
        try
        {
            var results = await client.GetAsync<Results>("/results", false, ct);
            if (results.GameId == gameId) Finished?.Invoke(results);
            else Log?.Invoke($"Round {gameId} is no longer current. Available results belong to round {results.GameId}.");
        }
        catch (ApiException ex) when (ex.StatusCode == 404) { Log?.Invoke($"Round {gameId} is no longer current; results are unavailable."); }
        Log?.Invoke("Bot stopped. Claim a team in the new lobby to play again.");
        return null;
    }

    private async Task<JsonElement?> TryActionAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try { return await client.ActAsync(method, path, body, ct); }
        catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 409 && !ex.Detail.Contains("only allowed while the game is active"))
        {
            Log?.Invoke($"Action skipped; state changed: {ex.Message}");
            return null;
        }
    }

    public async Task<int> HarvestAndDefendAsync(CancellationToken ct = default)
    {
        var trees = (await client.GetAsync<TreeList>("/trees", ct: ct)).Trees.Where(t => t.Status == "healthy").ToList();
        foreach (var branch in trees.SelectMany(t => t.Branches).Where(b => b.Status == "poisoned"))
            await TryActionAsync(HttpMethod.Delete, $"/branches/{branch.Id}", null, ct);
        var harvested = 0;
        foreach (var branch in trees.SelectMany(t => t.Branches).Where(b => b.Status == "healthy" && b.Fruit > 0).OrderByDescending(b => b.Fruit))
        {
            var result = await TryActionAsync(HttpMethod.Patch, $"/branches/{branch.Id}", new AmountRequest(branch.Fruit), ct);
            if (result is JsonElement value) harvested += value.GetProperty("harvested").GetInt32();
        }
        return harvested;
    }

    private async Task<bool> EnsureSeedsAsync(Inventory inventory, CancellationToken ct)
    {
        if (inventory.Seeds >= 3) return true;
        var needed = (3 - inventory.Seeds + 4) / 5;
        return inventory.Fruit >= needed && await TryActionAsync(HttpMethod.Patch, "/inventory/fruit", new ConvertRequest(needed, "seeds"), ct) is not null;
    }

    public async Task GrowAsync(BotOptions options, CancellationToken ct = default)
    {
        options.Validate();
        var snapshot = await SnapshotAsync(ct);
        foreach (var tree in snapshot.Trees.Where(t => t.Status == "dead"))
        {
            var inventory = await client.GetAsync<Inventory>("/me/inventory", ct: ct);
            if (!await EnsureSeedsAsync(inventory, ct)) continue;
            if (await TryActionAsync(HttpMethod.Delete, $"/trees/{tree.Id}", null, ct) is not null)
                await TryActionAsync(HttpMethod.Put, $"/plots/{tree.PlotId}/tree", null, ct);
        }
        var plots = (await client.GetAsync<PlotList>("/me/plots", ct: ct)).Plots;
        var occupied = plots.Count(p => p.TreeId is not null);
        var allowance = options.MaxTrees == 0 ? int.MaxValue : Math.Max(0, options.MaxTrees - occupied);
        foreach (var plot in plots.Where(p => p.TreeId is null).Take(allowance))
        {
            var inventory = await client.GetAsync<Inventory>("/me/inventory", ct: ct);
            if (!await EnsureSeedsAsync(inventory, ct)) return;
            if (await TryActionAsync(HttpMethod.Put, $"/plots/{plot.Id}/tree", null, ct) is null) return;
        }
        plots = (await client.GetAsync<PlotList>("/me/plots", ct: ct)).Plots;
        if (plots.Any(p => p.TreeId is null) || options.MaxTrees > 0 && plots.Count >= options.MaxTrees) return;
        var prices = await client.GetAsync<Market>("/market", false, ct);
        var price = prices.Items.Single(i => i.Item == "plot").Price;
        var batch = options.MaxTrees == 0 ? options.ExpansionBatch : Math.Min(options.ExpansionBatch, options.MaxTrees - plots.Count);
        for (var i = 0; i < batch; i++)
        {
            var inventory = await client.GetAsync<Inventory>("/me/inventory", ct: ct);
            var seedFruit = Math.Max(0, (3 - inventory.Seeds + 4) / 5);
            var moneyFruit = Math.Max(0L, ((long)price + options.Reserve - inventory.Money + 9) / 10);
            if (inventory.Fruit < seedFruit + moneyFruit) return;
            if (seedFruit > 0 && await TryActionAsync(HttpMethod.Patch, "/inventory/fruit", new ConvertRequest(seedFruit, "seeds"), ct) is null) return;
            if (moneyFruit > 0 && await TryActionAsync(HttpMethod.Patch, "/inventory/fruit", new ConvertRequest((int)moneyFruit, "money"), ct) is null) return;
            // Upstream charges quantity*N but only creates ONE plot. Always buy one.
            var result = await TryActionAsync(HttpMethod.Post, "/market/buy", new BuyRequest("plot", 1), ct);
            if (result is not JsonElement purchase) return;
            foreach (var plotId in purchase.GetProperty("plot_ids").EnumerateArray())
                if (await TryActionAsync(HttpMethod.Put, $"/plots/{plotId.GetInt32()}/tree", null, ct) is null) return;
        }
    }
}
