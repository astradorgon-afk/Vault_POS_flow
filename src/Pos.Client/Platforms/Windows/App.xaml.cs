namespace Pos.Client.WinUI;

/// <summary>Provides the Windows application host for the MAUI client.</summary>
public partial class App : MauiWinUIApplication
{
    /// <summary>Initializes the Windows application.</summary>
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
