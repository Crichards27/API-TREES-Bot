using ApiTrees.Ui;

namespace ApiTrees.Maui;

public partial class App : Application
{
    private readonly PlayerController player;
    public App(PlayerController player)
    {
        InitializeComponent();
        this.player = player;
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "API Trees Bot" };
        window.Stopped += async (_, _) => await player.StopAsync();
        window.Destroying += async (_, _) => await player.StopAsync();
        return window;
    }
}
