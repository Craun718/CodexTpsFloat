using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;

namespace CodexTpsFloat.App;

public static class Program
{
    public static int Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        if (options == null) return 2;

        if (options.SnapshotPath != null)
            return SnapshotWriter.Write(options.SnapshotPath, options.CodexHome);

        using var singleInstance = SingleInstance.Create(
            OperatingSystem.IsWindows() ? @"Local\CodexTpsFloat.Panel.v1" : "CodexTpsFloat",
            () => App.ShowExistingPanel?.Invoke());
        if (!singleInstance.IsFirstInstance && options.CapturePath == null)
        {
            singleInstance.SignalExistingInstance();
            return 0;
        }

        try
        {
            App.Options = options;
            App.SingleInstance = singleInstance;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Codex TPS 启动失败：" + error);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

public sealed record CommandLineOptions(string? CapturePath, string? CodexHome, string? SnapshotPath)
{
    public static CommandLineOptions? Parse(IReadOnlyList<string> args)
    {
        string? capture = null, home = null, snapshot = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (i + 1 >= args.Count) return null;
            switch (args[i])
            {
                case "--capture": capture = Path.GetFullPath(args[++i]); break;
                case "--codex-home": home = Path.GetFullPath(args[++i]); break;
                case "--snapshot-json": snapshot = Path.GetFullPath(args[++i]); break;
                default: return null;
            }
        }
        return new(capture, home, snapshot);
    }
}
