using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTpsFloat;

/// <summary>
/// Keeps one panel per user and wakes the running instance on a second launch.
/// Windows uses named synchronization objects; Unix uses an exclusive lock file
/// plus an atomically replaced wake request because .NET does not provide named
/// event wait handles on those platforms.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex? mutex;
    readonly EventWaitHandle? windowsWake;
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

    static SingleInstance CreateWindows(string name, Action? showExisting)
    {
        string scopedName = $"Local\\{name}.Panel.v1";
        var mutex = new Mutex(true, scopedName, out bool first);
        try
        {
            var wake = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}.Show.v1");
            RegisteredWaitHandle? registration = null;
            if (first && showExisting != null)
                registration = ThreadPool.RegisterWaitForSingleObject(wake,
                    (_, _) => showExisting(), null, Timeout.Infinite, false);
            return new(first, mutex, wake, registration);
        }
        catch
        {
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
                if (File.Exists(wakePath)) File.Delete(wakePath);
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

            if (!File.Exists(wakePath)) continue;
            try
            {
                File.Delete(wakePath);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            showExisting();
        }
    }

    public void SignalExistingInstance()
    {
        if (OperatingSystem.IsWindows())
        {
            windowsWake?.Set();
            return;
        }

        if (unixWakePath == null) return;
        string temporary = unixWakePath + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(unixWakePath)!);
            File.WriteAllText(temporary, "show");
            File.Move(temporary, unixWakePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        windowsRegistration?.Unregister(null);
        windowsWake?.Dispose();
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
