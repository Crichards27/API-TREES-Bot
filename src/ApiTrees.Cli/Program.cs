using ApiTrees.Core;
using System.Text.Json;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        API Trees — C# player
        dotnet run --project src/ApiTrees.Cli -- --server https://HOST [options] command [arguments]

        Commands:
          health | teams | market | results        Public information
          claim apple                             Claim and save an available team
          bot [--team apple]                      Resume saved team or claim in the lobby
          status | plots | trees                  Inspect your orchard
          team orange                             Opponent money, trees and plot IDs
          harvest [branch-id amount]              Harvest one branch or defend/harvest all
          plant plot-id | prune branch-id | delete-tree tree-id
          buy plot|poison|molotov [quantity]       Plot quantity must be 1 (upstream bug)
          convert money|seeds amount
          poison plot-id | molotov plot-id         Manual attacks

        Options (can appear before or after the command):
          --server URL            Or API_TREES_SERVER environment variable
          --ca-cert root.crt      Trust the host's private CA; hostname still verified
          --insecure              Disable TLS verification for trusted-LAN testing
          --timeout 10            HTTP timeout in seconds
          --session-dir PATH      Defaults to ~/.api_trees (reads Python sessions too)
          --team apple            Team to claim when no current saved session exists
          --interval 2 --max-trees 0 --reserve 0 --expansion-batch 4
          --round-seconds 600      Estimated time remaining from first active observation
          --bank-only             Harvest/defend without investing (--farm-only alias)

        Ctrl+C stops the bot. Tokens stay in the session file; protect that directory.
        A stale saved team is never automatically claimed in a new round: use claim TEAM.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var flags = new HashSet<string> { "insecure", "bank-only", "farm-only" };
    var values = new HashSet<string> { "server", "ca-cert", "timeout", "session-dir", "team", "interval", "max-trees", "reserve", "expansion-batch", "round-seconds" };
    var options = new Dictionary<string, string>();
    var positional = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) { positional.Add(args[i]); continue; }
        var key = args[i][2..];
        if (flags.Contains(key)) options.Add(key, "true");
        else if (values.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) options.Add(key, args[++i]);
        else throw new ArgumentException($"Unknown option or missing value: --{key}");
    }
    string? Option(string name) => options.GetValueOrDefault(name);
    int Number(string name, int fallback, int minimum = 0)
    {
        if (Option(name) is not string text) return fallback;
        if (!int.TryParse(text, out var number) || number < minimum) throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
        return number;
    }
    if (positional.Count == 0) throw new ArgumentException("Supply a command; see --help.");
    var command = positional[0];
    var parameters = positional.Skip(1).ToArray();
    void Count(int min, int max)
    {
        if (parameters.Length < min || parameters.Length > max) throw new ArgumentException($"Invalid arguments for {command}; see --help.");
    }
    int Positive(int index)
    {
        if (!int.TryParse(parameters[index], out var n) || n < 1) throw new ArgumentException("IDs, amounts and quantities must be positive integers.");
        return n;
    }
    var known = new HashSet<string> { "health", "teams", "market", "results", "team", "claim", "bot", "status", "plots", "trees", "harvest", "plant", "prune", "delete-tree", "buy", "convert", "poison", "molotov" };
    if (!known.Contains(command)) throw new ArgumentException($"Unknown command: {command}");
    var server = Option("server") ?? Environment.GetEnvironmentVariable("API_TREES_SERVER") ?? throw new ArgumentException("Supply --server https://HOST.");
    using var client = new GameClient(server, Option("ca-cert"), options.ContainsKey("insecure"), TimeSpan.FromSeconds(Number("timeout", 10, 1)));
    if (options.ContainsKey("insecure")) Console.Error.WriteLine("TLS verification disabled.");
    if (client.Server.StartsWith("http://")) Console.Error.WriteLine("HTTP sends tokens without encryption. Prefer HTTPS.");
    var ct = cancellation.Token;
    void Show(JsonElement element) => Console.WriteLine(JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true }));
    if (command is "health" or "teams" or "market" or "results")
    {
        Count(0, 0);
        Show(await client.GetAsync<JsonElement>("/" + command, false, ct));
        return 0;
    }
    if (command == "team")
    {
        Count(1, 1);
        Show(await client.GetAsync<JsonElement>("/team/" + Uri.EscapeDataString(parameters[0]), false, ct));
        return 0;
    }
    var store = new FileSessionStore(Option("session-dir"));
    var session = await store.LoadAsync(client.Server);
    if (command == "claim")
    {
        Count(1, 1);
        var lobby = await client.GetAsync<TeamList>("/teams", false, ct);
        if (session?.GameId == lobby.GameId) throw new InvalidOperationException($"Already have team {session.Team} in this round. Run bot to resume it.");
        session = await TeamSessions.ClaimAsync(client, lobby, parameters[0], store, ct);
        Console.WriteLine($"Claimed {session.Team} for round {session.GameId}; token saved.");
        return 0;
    }
    var botOptions = new BotOptions(Number("interval", 2, 1), Number("max-trees", 0), Number("reserve", 0),
        Number("expansion-batch", 4, 1), Option("round-seconds") is null ? null : Number("round-seconds", 600, 1),
        options.ContainsKey("bank-only") || options.ContainsKey("farm-only"));
    botOptions.Validate();
    if (session is null && command == "bot")
    {
        Count(0, 0);
        var lobby = await client.GetAsync<TeamList>("/teams", false, ct);
        var name = Option("team") ?? lobby.Teams.FirstOrDefault(t => t.Available)?.Name ?? throw new InvalidOperationException("No available teams.");
        session = await TeamSessions.ClaimAsync(client, lobby, name, store, ct);
        Console.WriteLine($"Claimed {session.Team}; token saved.");
    }
    if (session is null) throw new InvalidOperationException("Claim a team first.");
    client.Token = session.Token;
    var identity = await client.GetAsync<Identity>("/me", ct: ct);
    if (!identity.Authenticated || identity.Team != session.Team) throw new InvalidOperationException("Saved session identity does not match the server.");
    var engine = new BotEngine(client);
    engine.Log += Console.WriteLine;
    engine.Finished += results => Console.WriteLine(JsonSerializer.Serialize(results, GameJson.Default.Results));
    if (command == "bot")
    {
        Count(0, 0);
        Console.WriteLine($"Playing {session.Team}, round {session.GameId}. Ctrl+C stops safely.");
        await engine.RunAsync(session, botOptions, ct);
        return 0;
    }
    if (command is "status" or "plots" or "trees")
    {
        Count(0, 0);
        Show(await client.GetAsync<JsonElement>(command == "status" ? "/me/inventory" : command == "plots" ? "/me/plots" : "/trees", ct: ct));
        return 0;
    }
    // All manual writes must belong to the saved team's current round.
    var current = await client.GetAsync<TeamList>("/teams", false, ct);
    if (current.GameId != session.GameId || current.Status != "active") throw new InvalidOperationException("Saved team's round is not currently active.");
    switch (command)
    {
        case "harvest":
            if (parameters.Length == 0) Console.WriteLine($"Harvested {await engine.HarvestAndDefendAsync(ct)} fruit.");
            else { Count(2, 2); Show(await client.ActAsync(HttpMethod.Patch, $"/branches/{Positive(0)}", new AmountRequest(Positive(1)), ct)); }
            break;
        case "plant": case "prune": case "delete-tree":
            Count(1, 1);
            var path = command == "plant" ? $"/plots/{Positive(0)}/tree" : command == "prune" ? $"/branches/{Positive(0)}" : $"/trees/{Positive(0)}";
            Show(await client.ActAsync(command == "plant" ? HttpMethod.Put : HttpMethod.Delete, path, ct: ct));
            break;
        case "buy":
            Count(1, 2);
            if (parameters[0] is not ("plot" or "poison" or "molotov")) throw new ArgumentException("Buy plot, poison or molotov.");
            var quantity = parameters.Length == 1 ? 1 : Positive(1);
            if (parameters[0] == "plot" && quantity != 1) throw new ArgumentException("Buy plots one at a time: upstream creates only one plot per request.");
            Show(await client.ActAsync(HttpMethod.Post, "/market/buy", new BuyRequest(parameters[0], quantity), ct));
            break;
        case "convert":
            Count(2, 2);
            if (parameters[0] is not ("money" or "seeds")) throw new ArgumentException("Convert to money or seeds.");
            Show(await client.ActAsync(HttpMethod.Patch, "/inventory/fruit", new ConvertRequest(Positive(1), parameters[0]), ct));
            break;
        case "poison": case "molotov":
            Count(1, 1);
            Show(await client.ActAsync(HttpMethod.Post, "/attacks/" + command, new AttackRequest(Positive(0)), ct));
            break;
    }
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Stopped. Session retained; refresh state before restarting interrupted actions.");
    return 0;
}
catch (SessionSaveException ex)
{
    // A claim cannot be replayed. Preserve its issued token in a separate private
    // directory if the requested session location was unwritable.
    var recoveryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "API-Trees-Bot", "recovery");
    try
    {
        await new FileSessionStore(recoveryDirectory).SaveAsync(ex.Session);
        Console.Error.WriteLine($"Team {ex.Session.Team} was claimed. The requested session save failed; a private recovery copy was saved in {recoveryDirectory}.");
        Console.Error.WriteLine("Resume with --session-dir pointing to that directory. Do not claim the team again.");
    }
    catch
    {
        Console.Error.WriteLine("Team claimed but both session and recovery storage failed. The issued token could not be retained. Ask the host to reset this team.");
    }
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
