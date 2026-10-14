using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using CodexTpsFloat;

static class TrayLifecycleTests
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static object? Field(MainWindow window, string name) => typeof(MainWindow)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
    static string Usage(string id) => JsonSerializer.Serialize(new
    {
        timestamp = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"), type = "token_usage_record",
        payload = new { thread_id = "test-thread", turn_id = "test-turn", response_id = id,
            usage = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = 10, reasoning_output_tokens = 3 } }
    }) + "\n";
    [STAThread]
    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-tray-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        string file = Path.Combine(root, "sessions", "fixture.jsonl");
        File.WriteAllText(file, Usage("test-response-1"));
        int result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(codexHome: root);
        app.Startup += async (_, _) =>
        {
            try
            {
                await window.StartAsync();
                Check(!window.IsVisible && !window.ShowInTaskbar, "启动必须隐藏且不占任务栏");
                Check(((System.Windows.Forms.NotifyIcon)Field(window, "tray")!).Visible, "隐藏启动仍显示托盘图标");
                Check(((UsageSnapshot)Field(window, "snapshot")!).Records.Count == 1, "未打开面板也读取用量");
                if (!args.Contains("--never-show"))
                {
                    window.ShowPanel();
                    Check(window.IsVisible, "托盘可展开面板");
                    var handle = new WindowInteropHelper(window).Handle;
                    Check(handle != IntPtr.Zero && (GetWindowLongPtr(handle, -20).ToInt64() & 0x40000) == 0,
                        "真实窗口不能拥有 WS_EX_APPWINDOW 任务栏样式");
                    window.Hide();
                    Check(!window.IsVisible, "面板可收起");
                }
                File.AppendAllText(file, Usage("test-response-2"));
                await (Task)typeof(MainWindow).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                Check(((UsageSnapshot)Field(window, "snapshot")!).Records.Count == 2, "隐藏时继续增量读取");
                Console.WriteLine("通过：隐藏启动、托盘存在、后台读取、任务栏样式、展开收起、隐藏后刷新与正常退出。");
            }
            catch (Exception e) { result = 1; Console.Error.WriteLine(e); }
            finally { window.Close(); }
        };
        try { app.Run(); return result; }
        finally { Directory.Delete(root, true); }
    }
}
