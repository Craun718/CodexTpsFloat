using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CodexTpsFloat.App;

public partial class App : Application
{
    public static CommandLineOptions Options { get; set; } = new(null, null, null);
    public static SingleInstance? SingleInstance { get; set; }
    public static Action? ShowExistingPanel { get; set; }
    MainWindow? window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window = new MainWindow(Options.CapturePath, Options.CodexHome);

        if (Options.CapturePath == null)
            ShowExistingPanel = () => Dispatcher.UIThread.Post(window.ShowPanel);

        desktop.Startup += async (_, _) =>
        {
            await window.StartAsync();
            if (Options.CapturePath != null)
                await window.CaptureAndCloseAsync(Options.CapturePath);
            // Avalonia can create a tray icon on macOS without macOS displaying it,
            // so there is no reliable way to detect an unusable menu bar item.
            else if (OperatingSystem.IsMacOS() || !window.HasTray)
                window.ShowPanel();
        };

        base.OnFrameworkInitializationCompleted();
    }
}
