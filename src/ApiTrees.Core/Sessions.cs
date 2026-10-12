using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ApiTrees.Core;

public interface ISessionStore
{
    Task<Session?> LoadAsync(string server);
    Task SaveAsync(Session session);
}

public sealed class SessionSaveException(Session session, Exception inner)
    : Exception("Team claimed, but its token could not be saved. Keep this app open and retry Save session; do not claim again.", inner)
{
    public Session Session { get; } = session;
}

public static class SessionKeys
{
    public static string ForServer(string server) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GameClient.NormalizeServer(server)))).ToLowerInvariant()[..20];
}

// CLI sessions retain the legacy Python file location and snake_case format.
public sealed class FileSessionStore(string? directory = null) : ISessionStore
{
    private readonly string directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".api_trees");
    private string FileFor(string server) => Path.Combine(directory, SessionKeys.ForServer(server) + ".json");

    public async Task<Session?> LoadAsync(string server)
    {
        var path = FileFor(server);
        if (!File.Exists(path)) return null;
        var session = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), GameJson.Default.Session);
        if (session is null || GameClient.NormalizeServer(session.Server) != GameClient.NormalizeServer(server) ||
            string.IsNullOrWhiteSpace(session.Token) || session.GameId <= 0)
            throw new InvalidOperationException("Invalid session or a session belonging to another server.");
        return session;
    }

    public async Task SaveAsync(Session session)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = FileFor(session.Server);
        var temporary = Path.Combine(directory, Guid.NewGuid() + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, session, GameJson.Default.Session);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class TeamSessions
{
    public static async Task<Session> ClaimAsync(GameClient client, TeamList lobby, string team, ISessionStore store, CancellationToken ct = default)
    {
        if (lobby.Status != "lobby") throw new InvalidOperationException("Teams can only be claimed in the lobby.");
        if (!lobby.Teams.Any(t => t.Name == team && t.Available)) throw new InvalidOperationException("Select an available team.");
        var selected = await client.CallAsync<TeamSelection>(HttpMethod.Post, "/teams/select", new SelectRequest(team), false, ct);
        var session = new Session(client.Server, selected.Team, selected.Token, lobby.GameId);
        // Keep the issued token usable in memory even if persistence fails.
        client.Token = session.Token;
        try { await store.SaveAsync(session); }
        catch (Exception ex) { throw new SessionSaveException(session, ex); }
        return session;
    }
}
