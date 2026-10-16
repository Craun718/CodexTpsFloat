using System;
using System.IO;
using System.Linq;

namespace CodexTpsFloat;

public sealed class PanelSettings
{
    public static readonly int[] AllowedWindowSeconds = { 60, 300, 1800, 3600 };
    public static readonly int[] AllowedRefreshSeconds = { 5, 15, 30, 60 };

    public string CodexHome { get; set; } = DefaultCodexHome();
    public int WindowSeconds { get; set; } = 300;
    public int RefreshSeconds { get; set; } = 15;
    public bool Pinned { get; set; } = true;
    public double? X { get; set; }
    public double? Y { get; set; }

    public static string DefaultCodexHome() => Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Replaces values that are out of range or that predate an option set.</summary>
    public void Normalize()
    {
        if (!AllowedWindowSeconds.Contains(WindowSeconds)) WindowSeconds = 300;
        if (!AllowedRefreshSeconds.Contains(RefreshSeconds)) RefreshSeconds = 15;
        if (string.IsNullOrWhiteSpace(CodexHome)) CodexHome = DefaultCodexHome();
    }
}
