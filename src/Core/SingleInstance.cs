using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace CodexTpsFloat;

/// <summary>
/// Keeps one panel per user and wakes the running instance on a second launch.
/// Windows uses named synchronization objects; Unix uses an exclusive lock file
/// plus per-request wake files and acknowledgments because .NET does not provide named
/// event wait handles on those platforms.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex? mutex;
    readonly EventWaitHandle? windowsWake;
    EventWaitHandle? windowsAcknowledged;
    readonly RegisteredWaitHandle? windowsRegistration;
    readonly FileStream? unixLock;
    readonly string? unixWakePath;
    readonly CancellationTokenSource? unixCancellation;
    readonly Task? unixWakeLoop;
    readonly bool ownsInstance;
    bool disposed;

    SingleInstance(bool ownsInstance, Mutex? mutex = null,
        EventWaitHandle? windowsWake = null,
        RegisteredWaitHandle? windowsRegistration = null,
        FileStream? unixLock = null,
        string? unixWakePath = null,
        CancellationTokenSource? unixCancellation = null,
        Task? unixWakeLoop = null)
    {
        this.ownsInstance = ownsInstance;
        this.mutex = mutex;
        this.windowsWake = windowsWake;
        this.windowsRegistration = windowsRegistration;
        this.unixLock = unixLock;
        this.unixWakePath = unixWakePath;
        this.unixCancellation = unixCancellation;
        this.unixWakeLoop = unixWakeLoop;
    }

    public bool IsFirstInstance => ownsInstance;

    public static SingleInstance Create(string name, Action? showExisting = null,
        string? stateDirectory = null)
    {
        if (OperatingSystem.IsWindows())
            return CreateWindows(name, showExisting);
        return CreateUnix(name, showExisting, stateDirectory);
    }

    [SupportedOSPlatform("windows")]
    static SingleInstance CreateWindows(string name, Action? showExisting)
    {
        string scopedName = $"Local\\{name}.Panel.v1";
        // Hold the wake handle before claiming ownership so an early secondary
        // cannot signal and close the only handle before the owner opens it.
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}.Show.v1");
        var mutex = new Mutex(true, scopedName, out bool first);
        try
        {
            EventWaitHandle? acknowledged = null;
            if (first) acknowledged = new EventWaitHandle(false, EventResetMode.AutoReset, scopedName + ".Acknowledged");
            else
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (EventWaitHandle.TryOpenExisting(scopedName + ".Acknowledged", out acknowledged)) break;
                    Thread.Sleep(10);
                }
            }
            RegisteredWaitHandle? registration = null;
            if (first && showExisting != null)
                registration = ThreadPool.RegisterWaitForSingleObject(wake,
                    (_, _) =>
                    {
                        try { showExisting(); acknowledged?.Set(); }
                        catch (Exception error) { Console.Error.WriteLine("显示统计面板失败：" + error.Message); }
                    }, null, Timeout.Infinite, false);
            return new(first, mutex, wake, registration) { windowsAcknowledged = acknowledged };
        }
        catch
        {
            wake.Dispose();
            if (first) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    static SingleInstance CreateUnix(string name, Action? showExisting, string? stateDirectory)
    {
        string scopedName = $"{Environment.UserName}.{name}";
        string directory = stateDirectory ?? PlatformPaths.UserCacheDirectory();
        Directory.CreateDirectory(directory);
        string key = InstanceKey(scopedName);
        string lockPath = Path.Combine(directory, $"instance-{key}.lock");
        string wakePath = Path.Combine(directory, $"instance-{key}.wake");

        FileStream? lockFile = null;
        bool owns = false;
        try
        {
            try
            {
                lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);
                owns = true;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 35)
            {
            }

            if (!owns)
                return new(false, unixWakePath: wakePath);

            CancellationTokenSource? cancellation = null;
            try
            {
                cancellation = new CancellationTokenSource();
                Task? wakeLoop = showExisting == null
                    ? null
                    : WatchWakeRequestsAsync(wakePath, cancellation.Token, showExisting);
                return new(true, unixLock: lockFile, unixWakePath: wakePath,
                    unixCancellation: cancellation, unixWakeLoop: wakeLoop);
            }
            catch
            {
                cancellation?.Dispose();
                throw;
            }
        }
        catch
        {
            lockFile?.Dispose();
            throw;
        }
    }

    static string InstanceKey(string scopedName) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(scopedName))).ToLowerInvariant()[..16];

    static async Task WatchWakeRequestsAsync(string wakePath, CancellationToken cancellation,
        Action showExisting)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                foreach (string request in Directory.EnumerateFiles(Path.GetDirectoryName(wakePath)!,
                    Path.GetFileName(wakePath) + ".*.request"))
                {
                    if (cancellation.IsCancellationRequested) return;
                    // Expired requests belong to clients that have already reported failure.
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(request) > TimeSpan.FromSeconds(30))
                    {
                        File.Delete(request);
                        continue;
                    }
                    showExisting();
                    File.Move(request, request + ".ack", true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
            {
                Console.Error.WriteLine("处理面板显示请求失败：" + error.Message);
            }
        }
    }

    public void SignalExistingInstance(TimeSpan? acknowledgmentTimeout = null)
    {
        if (OperatingSystem.IsWindows())
        {
            windowsAcknowledged?.Reset();
            windowsWake?.Set();
            // Older WPF instances use the same wake event but do not acknowledge it.
            if (windowsAcknowledged != null && !windowsAcknowledged.WaitOne(acknowledgmentTimeout ?? TimeSpan.FromSeconds(12)))
                throw new TimeoutException("已有实例未确认显示请求，请稍后重试。");
            return;
        }

        if (unixWakePath == null) throw new InvalidOperationException("没有可用的实例通信路径。");
        string request = unixWakePath + "." + Guid.NewGuid().ToString("N") + ".request";
        string temporary = request + ".tmp";
        string acknowledged = request + ".ack";
        try
        {
            File.WriteAllText(temporary, "show");
            File.Move(temporary, request);
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (timeout.Elapsed < (acknowledgmentTimeout ?? TimeSpan.FromSeconds(12)))
            {
                if (File.Exists(acknowledged)) return;
                Thread.Sleep(50);
            }
            throw new TimeoutException("已有实例未确认显示请求，请稍后重试。");
        }
        finally
        {
            foreach (string path in new[] { temporary, request, acknowledged })
            {
                try { File.Delete(path); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        windowsRegistration?.Unregister(null);
        windowsWake?.Dispose();
        windowsAcknowledged?.Dispose();
        unixCancellation?.Cancel();
        if (unixWakeLoop != null)
        {
            try { unixWakeLoop.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        unixCancellation?.Dispose();
        if (ownsInstance)
        {
            if (unixWakePath != null && File.Exists(unixWakePath))
                File.Delete(unixWakePath);
            mutex?.ReleaseMutex();
        }
        unixLock?.Dispose();
        mutex?.Dispose();
    }
}
