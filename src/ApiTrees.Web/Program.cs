using ApiTrees.Core;
using ApiTrees.Ui;
using ApiTrees.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// This optional developer host stores tokens only for the circuit's lifetime.
// The installed MAUI application uses native secure storage instead.
builder.Services.AddScoped<ISessionStore, MemorySessions>();
builder.Services.AddScoped<ICertificatePicker, NoFilePicker>();
builder.Services.AddScoped<PlayerController>();
var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Dashboard).Assembly);
app.Run();

sealed class MemorySessions : ISessionStore
{
    private readonly Dictionary<string, Session> sessions = new();
    public Task<Session?> LoadAsync(string server) => Task.FromResult(sessions.GetValueOrDefault(server));
    public Task SaveAsync(Session session) { sessions[session.Server] = session; return Task.CompletedTask; }
}
sealed class NoFilePicker : ICertificatePicker
{
    public Task<string?> PickAsync() => Task.FromResult<string?>(null);
}
