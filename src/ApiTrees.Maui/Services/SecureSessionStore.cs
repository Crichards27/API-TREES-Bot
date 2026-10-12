using System.Text.Json;
using ApiTrees.Core;
using ApiTrees.Ui;

namespace ApiTrees.Maui.Services;

public sealed class SecureSessionStore : ISessionStore
{
    public async Task<Session?> LoadAsync(string server)
    {
        var json = await SecureStorage.Default.GetAsync("api-trees-" + SessionKeys.ForServer(server));
        if (json is null) return null;
        var session = JsonSerializer.Deserialize(json, GameJson.Default.Session);
        if (session is null || session.Server != GameClient.NormalizeServer(server) || string.IsNullOrWhiteSpace(session.Token) || session.GameId <= 0)
            throw new InvalidOperationException("Saved session is invalid or belongs to another server.");
        return session;
    }

    public Task SaveAsync(Session session) => SecureStorage.Default.SetAsync(
        "api-trees-" + SessionKeys.ForServer(session.Server), JsonSerializer.Serialize(session, GameJson.Default.Session));
}

public sealed class CertificatePicker : ICertificatePicker
{
    public async Task<string?> PickAsync()
    {
        var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Select the game host's root.crt certificate" });
        if (picked is null) return null;
        // Android/iOS file results may be content URIs; copy through OpenReadAsync.
        var directory = Path.Combine(FileSystem.AppDataDirectory, "certificates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid() + ".crt");
        await using var input = await picked.OpenReadAsync();
        await using var output = File.Create(path);
        await input.CopyToAsync(output);
        return path;
    }
}
