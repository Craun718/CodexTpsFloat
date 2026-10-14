using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace CodexTpsFloat;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? capture = null, home = null, snapshotPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) return 2;
            switch (args[i])
            {
                case "--capture": capture = Path.GetFullPath(args[++i]); break;
                case "--codex-home": home = Path.GetFullPath(args[++i]); break;
                case "--snapshot-json": snapshotPath = Path.GetFullPath(args[++i]); break;
                default: return 2;
            }
        }
        try
        {
            if (snapshotPath != null)
            {
                home ??= Environment.GetEnvironmentVariable("CODEX_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                var clockReader = new RequestClockReader(home);
                var scanner = new UsageScanner(home) { RequestStarts = clockReader.Refresh() };
                var snapshot = scanner.Refresh(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)));
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                File.WriteAllText(snapshotPath, JsonSerializer.Serialize(new { snapshot.GeneratedAt, snapshot.Files, snapshot.InvalidLines,
                    snapshot.UnsupportedFiles, snapshot.ReadFailures, snapshot.ScanMilliseconds, requestClockWarning = clockReader.Warning,
                    windows = new[] { UsageScanner.Window(snapshot, 60), UsageScanner.Window(snapshot, 300), UsageScanner.Window(snapshot, 1800), UsageScanner.Window(snapshot, 3600) },
                    hours = UsageScanner.Hours(snapshot, snapshot.GeneratedAt.Date) }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                return snapshot.Files == 0 ? 3 : snapshot.ReadFailures > 0 ? 1 : 0;
            }
            using var mutex = new Mutex(true, @"Local\CodexTpsFloat.Panel.v1", out bool first);
            using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexTpsFloat.Show.v1");
            if (!first && capture == null) { wake.Set(); return 0; }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(capture, home);
            RegisteredWaitHandle? registration = null;
            if (capture == null) registration = ThreadPool.RegisterWaitForSingleObject(wake,
                (_, _) => app.Dispatcher.BeginInvoke(window.ShowPanel), null, Timeout.Infinite, false);
            try { return app.Run(window); }
            finally { registration?.Unregister(null); if (first) mutex.ReleaseMutex(); }
        }
        catch (Exception e)
        {
            if (snapshotPath != null) return 1;
            MessageBox.Show("Codex TPS 启动失败：" + e.Message, "Codex TPS");
            return 1;
        }
    }
}
