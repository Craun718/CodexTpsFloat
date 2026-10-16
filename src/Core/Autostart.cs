using System;
using System.IO;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexTpsFloat;

/// <summary>
/// Registers the app to start at login. Each platform has its own mechanism:
/// Windows uses the Run key, macOS a LaunchAgent, Linux a desktop autostart entry.
/// </summary>
public interface IAutostartService
{
    bool IsEnabled();
    /// <summary>Enables or disables autostart; returns an error message on failure.</summary>
    string? SetEnabled(bool enabled);
}

public static class Autostart
{
    public static IAutostartService Create(string? executablePath = null, string? autostartDirectory = null)
    {
        string exe = executablePath ?? Environment.ProcessPath ?? "";
        if (OperatingSystem.IsWindows()) return new WindowsAutostart(exe);
        if (OperatingSystem.IsMacOS()) return new MacAutostart(exe);
        return new LinuxAutostart(exe, autostartDirectory ?? PlatformPaths.UserAutostartDirectory());
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAutostart : IAutostartService
{
    const string StartupName = "CodexTpsFloat";
    readonly string executable;
    public WindowsAutostart(string executable) => this.executable = executable;
    public bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(StartupName) is string value && value == $"\"{executable}\"";
    }
    public string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue(StartupName, $"\"{executable}\"");
            else key.DeleteValue(StartupName, false);
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { return e.Message; }
    }
}

public sealed class MacAutostart : IAutostartService
{
    readonly string executable;
    readonly string plistPath;
    public MacAutostart(string executable, string? launchAgentsDirectory = null) =>
        (this.executable, plistPath) = (executable, Path.Combine(
            launchAgentsDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
            "com.codextpsfloat.plist"));
    public bool IsEnabled() => File.Exists(plistPath);
    public string? SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled) { if (File.Exists(plistPath)) File.Delete(plistPath); return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
            string escaped = System.Security.SecurityElement.Escape(executable) ?? executable;
            string plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>com.codextpsfloat</string>
                  <key>ProgramArguments</key><array><string>{escaped}</string></array>
                  <key>RunAtLoad</key><true/>
                </dict>
                </plist>
                """;
            File.WriteAllText(plistPath, plist, new UTF8Encoding(false));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }
}

public sealed class LinuxAutostart : IAutostartService
{
    // Exec quoting is decoded after desktop-entry string escapes.
    static string EncodeExecutable(string path)
    {
        if (path.Contains('\0'))
            throw new ArgumentException("登录启动路径不能包含空字符。");
        var quoted = new StringBuilder();
        foreach (char c in path)
        {
            if (c is '\\' or '"' or '`' or '$') quoted.Append('\\');
            quoted.Append(c == '%' ? "%%" : c.ToString());
        }
        return quoted.ToString().Replace("\\", "\\\\").Replace("\n", "\\n")
            .Replace("\r", "\\r").Replace("\t", "\\t");
    }
    readonly string executable;
    readonly string desktopPath;
    public LinuxAutostart(string executable, string autostartDirectory) =>
        (this.executable, desktopPath) = (executable, Path.Combine(autostartDirectory, "CodexTpsFloat.desktop"));
    public bool IsEnabled() => File.Exists(desktopPath);
    public string? SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled) { if (File.Exists(desktopPath)) File.Delete(desktopPath); return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);
            string desktop = $"""
                [Desktop Entry]
                Type=Application
                Name=Codex TPS
                Exec="{EncodeExecutable(executable)}"
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """;
            File.WriteAllText(desktopPath, desktop, new UTF8Encoding(false));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return e.Message; }
    }
}
