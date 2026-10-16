using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CodexTpsFloat;
using AppWindow = CodexTpsFloat.App.MainWindow;

AppBuilder.Configure<CodexTpsFloat.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).WithInterFont().SetupWithoutStarting();
string root = Path.Combine(Path.GetTempPath(), "codex-export-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var scope = new Cleanup(root);
const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
var window = new AppWindow(capturePath: "test");
typeof(AppWindow).GetField("snapshot", flags)!.SetValue(window,
    new UsageSnapshot(DateTimeOffset.UtcNow, Array.Empty<TimedUsage>(), new Dictionary<string, TurnData>(), 1, 0, 0, 0, 0));
window.Show();
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); passed++; }
var dragGuard = typeof(AppWindow).GetMethod("HasInteractiveAncestor", BindingFlags.Static | BindingFlags.NonPublic)!;
Check((bool)dragGuard.Invoke(null, new object[] { new RadioButton() })!, "duration radio buttons are excluded from panel dragging");
Check((bool)dragGuard.Invoke(null, new object[] { new CheckBox() })!, "settings checkboxes are excluded from panel dragging");
Button[] Buttons(Window dialog) => ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.OfType<Button>().ToArray();
void Click(Window dialog, string label)
{
    Buttons(dialog).Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
}
void Export(string pickedPath, string pattern)
{
    var file = DispatchProxy.Create<IStorageFile, TestProxy>();
    ((TestProxy)(object)file).Handler = (m, a) => m.Name switch { "get_Path" => new Uri(pickedPath), "get_Name" => Path.GetFileName(pickedPath), _ => null };
    var provider = DispatchProxy.Create<IStorageProvider, TestProxy>();
    ((TestProxy)(object)provider).Handler = (m, a) =>
    {
        if (m.Name != "SaveFilePickerWithResultAsync") throw new Exception(m.Name);
        var options = (FilePickerSaveOptions)a![0]!;
        if (options.ShowOverwritePrompt != true) throw new Exception("Native overwrite prompt disabled");
        return Task.FromResult(new SaveFilePickerResult { File = file, SelectedFileType = options.FileTypeChoices!.Single(t => t.Patterns!.Contains(pattern)) });
    };
    typeof(TopLevel).GetField("_storageProvider", flags)!.SetValue(window, provider);
    typeof(AppWindow).GetMethod("ExportClick", flags)!.Invoke(window, new object?[] { null, new RoutedEventArgs() });
    Dispatcher.UIThread.RunJobs();
}
string picked = Path.Combine(root, "report.md"), target = Path.Combine(root, "report.json");
const string original = "existing data";
File.WriteAllText(target, original);
Export(picked, "*.json");
var dialog = window.OwnedWindows.Single();
Check(Buttons(dialog).Length == 2 && File.ReadAllText(target) == original, "extension change prompts before touching existing JSON");
Click(dialog, "取消");
Check(File.ReadAllText(target) == original && !window.OwnedWindows.Any(), "Cancel preserves existing JSON and ends export");
Export(picked, "*.json");
dialog = window.OwnedWindows.Single();
dialog.Close(); Dispatcher.UIThread.RunJobs();
Check(File.ReadAllText(target) == original, "closing confirmation preserves existing JSON");
Export(picked, "*.json");
Click(window.OwnedWindows.Single(), "覆盖");
Check(File.ReadAllText(target).TrimStart('\uFEFF').StartsWith("{") && !File.Exists(picked), "explicit approval writes JSON to normalized target");
Click(window.OwnedWindows.Single(), "确定");
File.Delete(target);
Export(picked, "*.json");
Check(File.Exists(target) && Buttons(window.OwnedWindows.Single()).Length == 1, "new normalized target needs no overwrite confirmation");
Click(window.OwnedWindows.Single(), "确定");
Export(target, "*.json");
Check(Buttons(window.OwnedWindows.Single()).Length == 1, "unchanged path relies on native overwrite confirmation");
Click(window.OwnedWindows.Single(), "确定");
File.WriteAllText(picked, original);
Export(target, "*.md");
Check(File.ReadAllText(picked) == original && Buttons(window.OwnedWindows.Single()).Length == 2, "JSON-to-Markdown normalization also confirms overwrite");
Click(window.OwnedWindows.Single(), "取消");
window.Close();
Console.WriteLine($"All {passed} Avalonia regression checks passed without native UI.");

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? m, object?[]? a) => Handler(m!, a);
}
public sealed record Cleanup(string Root) : IDisposable { public void Dispose() => Directory.Delete(Root, true); }
