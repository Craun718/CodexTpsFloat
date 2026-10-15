using System;
using System.IO;
using System.Text.Json;

namespace CodexTpsFloat;

/// <summary>
/// Persists panel settings. Portable Windows builds keep the file beside the executable;
/// other platforms use the conventional per-user application data directory, because a
/// signed macOS bundle or a /usr install directory is not writable.
/// </summary>
public sealed class SettingsStore
{
    readonly string path;
    public SettingsStore(string path) => this.path = path;
    public string Path => path;
    public static SettingsStore ForPortableApp(string baseDirectory) =>
        new(System.IO.Path.Combine(baseDirectory, "data", "settings.json"));
    public static SettingsStore ForUserProfile(string appName = "CodexTpsFloat")
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return new(System.IO.Path.Combine(root, appName, "settings.json"));
    }
    public PanelSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(path)) ?? new PanelSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { return new PanelSettings(); }
    }
    /// <summary>Writes atomically; returns an error message when saving fails.</summary>
    public string? Save(PanelSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }
}
