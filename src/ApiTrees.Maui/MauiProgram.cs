using ApiTrees.Core;
using ApiTrees.Ui;
using ApiTrees.Maui.Services;
using Microsoft.Extensions.Logging;

namespace ApiTrees.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(FileSystem.AppDataDirectory, "WebView2"));
#endif
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<ISessionStore, SecureSessionStore>();
        builder.Services.AddSingleton<ICertificatePicker, CertificatePicker>();
        builder.Services.AddSingleton<PlayerController>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
