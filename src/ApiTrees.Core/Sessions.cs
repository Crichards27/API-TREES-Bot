using System.Security.Cryptography;
using System.Text;

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

public static class TeamSessions
{
    public static async Task<Session> ClaimAsync(GameClient client, TeamList lobby, string team, ISessionStore store, CancellationToken ct = default)
    {
        if (lobby.Status != "lobby") throw new InvalidOperationException("Teams can only be claimed in the lobby.");
        if (!lobby.Teams.Any(t => t.Name == team && t.Available)) throw new InvalidOperationException("Select an available team.");
        var selected = await client.CallAsync<TeamSelection>(HttpMethod.Post, "/teams/select", new SelectRequest(team), false, ct);
        var session = new Session(client.Server, selected.Team, selected.Token, lobby.GameId) { RoundUnconfirmed = true };
        // Retain the issued token before any more reads or persistence can fail.
        client.Token = session.Token;
        try
        {
            var after = await client.GetAsync<TeamList>("/teams", false, ct);
            // Pinned upstream creates monotonically increasing game IDs. Only an
            // unchanged bracket around select confirms the pre-claim observation.
            // /me authenticates old-round tokens too and supplies no game identity.
            if (after.GameId == lobby.GameId) session = session with { RoundUnconfirmed = false };
        }
        catch (Exception)
        {
            // Failure to verify must not lose an issued token or prevent saving it.
        }
        try { await store.SaveAsync(session); }
        catch (Exception ex) { throw new SessionSaveException(session, ex); }
        return session;
    }
}
