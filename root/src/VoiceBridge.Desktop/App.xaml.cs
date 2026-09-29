using Microsoft.UI.Xaml;
using VoiceBridge.Desktop.Diagnostics;

namespace VoiceBridge.Desktop;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    internal string? CrashLogDirectory { get; set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        _ = LocalCrashLog.TryWrite(CrashLogDirectory, args.Exception);
    }
}
