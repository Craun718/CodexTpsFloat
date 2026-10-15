using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTpsFloat;

/// <summary>
/// Keeps one panel per user and wakes the running instance on a second launch.
/// Windows uses named synchronization objects; Unix uses a Unix domain socket
/// because .NET does not support named event wait handles on those platforms.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex mutex;
    readonly bool ownsMutex;
    readonly EventWaitHandle? windowsWake;
    readonly RegisteredWaitHandle? windowsRegistration;
    readonly Socket? unixListener;
    readonly string? unixSocketPath;
    readonly CancellationTokenSource? unixCancellation;
    readonly Task? unixAcceptLoop;
    bool disposed;

    SingleInstance(Mutex mutex, bool ownsMutex,
        EventWaitHandle? windowsWake = null,
        RegisteredWaitHandle? windowsRegistration = null,
        Socket? unixListener = null,
        string? unixSocketPath = null,
        CancellationTokenSource? unixCancellation = null,
        Task? unixAcceptLoop = null)
    {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
        this.windowsWake = windowsWake;
        this.windowsRegistration = windowsRegistration;
        this.unixListener = unixListener;
        this.unixSocketPath = unixSocketPath;
        this.unixCancellation = unixCancellation;
        this.unixAcceptLoop = unixAcceptLoop;
    }

    public bool IsFirstInstance => ownsMutex;

    public static SingleInstance Create(string name, Action? showExisting = null)
    {
        string scopedName = OperatingSystem.IsWindows()
            ? name
            : $"CodexTpsFloat.{Environment.UserName}.{name}";
        var mutex = new Mutex(true, scopedName + ".Panel", out bool first);
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateWindows(mutex, first, scopedName, showExisting)
                : CreateUnix(mutex, first, scopedName, showExisting);
        }
        catch
        {
            if (first) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    static SingleInstance CreateWindows(Mutex mutex, bool first, string scopedName,
        Action? showExisting)
    {
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, scopedName + ".Show");
        RegisteredWaitHandle? registration = null;
        if (first && showExisting != null)
            registration = ThreadPool.RegisterWaitForSingleObject(wake,
                (_, _) => showExisting(), null, Timeout.Infinite, false);
        return new(mutex, first, wake, registration);
    }

    static SingleInstance CreateUnix(Mutex mutex, bool first, string scopedName,
        Action? showExisting)
    {
        string socketPath = UnixSocketPath(scopedName);
        Socket? listener = null;
        CancellationTokenSource? cancellation = null;
        Task? acceptLoop = null;
        if (first)
        {
            try
            {
                if (File.Exists(socketPath)) File.Delete(socketPath);
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(1);
                cancellation = new CancellationTokenSource();
                if (showExisting != null)
                    acceptLoop = AcceptConnectionsAsync(listener, cancellation.Token, showExisting);
            }
            catch (Exception e) when (e is SocketException or IOException
                or UnauthorizedAccessException)
            {
                listener?.Dispose();
                listener = null;
                cancellation?.Dispose();
                cancellation = null;
            }
        }
        return new(mutex, first, unixListener: listener, unixSocketPath: socketPath,
            unixCancellation: cancellation, unixAcceptLoop: acceptLoop);
    }

    static string UnixSocketPath(string scopedName)
    {
        // Unix domain socket paths are limited to roughly 100 bytes. Keep the
        // temporary path bounded even when the logical instance name is long.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopedName)))
            .ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), $"CodexTpsFloat-{hash[..16]}.sock");
    }

    static async Task AcceptConnectionsAsync(Socket listener, CancellationToken cancellation,
        Action showExisting)
    {
        while (!cancellation.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellation);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException
                or SocketException)
            {
                return;
            }

            using (connection)
            {
                try
                {
                    var buffer = new byte[1];
                    await connection.ReceiveAsync(buffer, SocketFlags.None, cancellation);
                    showExisting();
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException
                    or SocketException)
                {
                    return;
                }
            }
        }
    }

    public void SignalExistingInstance()
    {
        if (OperatingSystem.IsWindows())
        {
            windowsWake?.Set();
            return;
        }

        if (unixSocketPath == null) return;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(unixSocketPath));
                socket.Send(new byte[] { 1 });
                return;
            }
            catch (SocketException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        windowsRegistration?.Unregister(null);
        windowsWake?.Dispose();
        unixCancellation?.Cancel();
        unixListener?.Dispose();
        if (unixAcceptLoop != null)
        {
            try { unixAcceptLoop.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        unixCancellation?.Dispose();
        if (ownsMutex && unixSocketPath != null && File.Exists(unixSocketPath))
            File.Delete(unixSocketPath);
        if (ownsMutex) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
