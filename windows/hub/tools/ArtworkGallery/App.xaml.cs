using AppleDeviceHub.App.Design;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ArtworkGallery;

/// <summary>
/// Review harness entry point. Arguments include --page=models|controls, --state=open|closed|play|loop|cues, --model=0x2720 [--colorway=id], --capture=&lt;absolute PNG path&gt;, and --result=&lt;path&gt;.
/// No tray, single-instance lock, settings file or Bluetooth.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            MainWindow.Fail(e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        EffectsPolicy.Initialize(DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _window.Activate();
    }
}
