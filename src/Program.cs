using System;
using System.IO;
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
                return SnapshotWriter.Write(snapshotPath, home);
            using var mutex = new Mutex(true, @"Local\CodexTpsFloat.Panel.v1", out bool first);
            using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexTpsFloat.Show.v1");
            if (!first && capture == null) { wake.Set(); return 0; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(capture, home);
            app.Startup += async (_, _) =>
            {
                await window.StartAsync();
                if (capture != null) await window.CaptureAndCloseAsync(capture);
            };
            RegisteredWaitHandle? registration = null;
            if (capture == null) registration = ThreadPool.RegisterWaitForSingleObject(wake,
                (_, _) => app.Dispatcher.BeginInvoke(window.ShowPanel), null, Timeout.Infinite, false);
            try { return app.Run(); }
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
