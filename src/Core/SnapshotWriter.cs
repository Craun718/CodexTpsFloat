using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexTpsFloat;

/// <summary>
/// Renders a machine-readable snapshot of the current usage without starting any UI.
/// Installed builds use this for smoke tests, and it is the only mode that runs headless.
/// </summary>
public static class SnapshotWriter
{
    public static int Write(string snapshotPath, string? codexHome)
    {
        string home = codexHome ?? PanelSettings.DefaultCodexHome();
        var clockReader = new RequestClockReader(home);
        var scanner = new UsageScanner(home) { RequestStarts = clockReader.Refresh() };
        var snapshot = scanner.Refresh(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath));
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(new
        {
            snapshot.GeneratedAt,
            snapshot.Files,
            snapshot.InvalidLines,
            snapshot.UnsupportedFiles,
            snapshot.ReadFailures,
            snapshot.ScanMilliseconds,
            requestClockWarning = clockReader.Warning,
            windows = new[]
            {
                UsageScanner.Window(snapshot, 60), UsageScanner.Window(snapshot, 300),
                UsageScanner.Window(snapshot, 1800), UsageScanner.Window(snapshot, 3600)
            },
            hours = UsageScanner.Hours(snapshot, snapshot.GeneratedAt.Date)
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return snapshot.Files == 0 ? 3 : snapshot.ReadFailures > 0 ? 1 : 0;
    }
}
