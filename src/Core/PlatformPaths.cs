using System;
using System.IO;

namespace CodexTpsFloat;

/// <summary>Per-user locations that follow each platform's conventions.</summary>
public static class PlatformPaths
{
    /// <summary>
    /// Windows returns %APPDATA%, macOS returns ~/Library/Application Support and Linux
    /// returns ~/.config, so this is also the right place to keep user settings.
    /// </summary>
    public static string UserConfigDirectory(string appName = "CodexTpsFloat")
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, appName);
    }
}
