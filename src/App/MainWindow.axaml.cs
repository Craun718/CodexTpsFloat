using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CodexTpsFloat.App;

public partial class MainWindow : Window
{
    readonly DispatcherTimer timer = new();
    readonly TrayIcon? tray;
    readonly IAutostartService autostart = Autostart.Create();
    readonly SettingsStore settingsStore = OperatingSystem.IsWindows()
        ? SettingsStore.ForPortableApp(AppContext.BaseDirectory)
        : SettingsStore.ForUserProfile();
    readonly string? capturePath;
    PanelSettings settings;
    UsageScanner scanner;
    RequestClockReader requestClocks;
    UsageSnapshot? snapshot;
    bool refreshing, initialized, closing, positioned, refreshPending;
    readonly List<string> models = new() { "全部模型" };

    public MainWindow() : this(null, null) { }

    public MainWindow(string? capturePath = null, string? codexHome = null)
    {
        this.capturePath = capturePath;
        settings = settingsStore.Load();
        if (codexHome != null) settings.CodexHome = Path.GetFullPath(codexHome);
        settings.Normalize();
        scanner = new(settings.CodexHome);
        requestClocks = new(settings.CodexHome);

        InitializeComponent();
        InitializeState();

        tray = OperatingSystem.IsBrowser() ? null : CreateTrayIcon();
        HideButton.IsVisible = HasTray;
        if (tray != null && Application.Current != null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });

        Opened += (_, _) => PositionPanel();
        Closing += (_, _) => DisposeWindow();
    }

    static DateTimeOffset BeijingNow => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));

    // Linux can create the object without a status-notifier host. Keep the panel
    // accessible until Avalonia exposes whether the host actually displays it.
    public bool HasTray => tray != null && !OperatingSystem.IsLinux();

    void InitializeState()
    {
        HomeBox.Text = settings.CodexHome;
        Topmost = settings.Pinned;
        TopmostBox.IsChecked = settings.Pinned;
        SetPinColor(settings.Pinned);
        DateBox.SelectedDate = BeijingNow.Date;
        StartupBox.IsChecked = autostart.IsEnabled();

        ModelBox.ItemsSource = models;
        ModelBox.SelectedIndex = 0;

        // Assign the selection instead of toggling IsSelected on every item: clearing a later
        // item's IsSelected would also clear the selection just set on an earlier one.
        foreach (var item in CadenceBox.Items.OfType<ComboBoxItem>())
            if (item.Tag?.ToString() == settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture))
                CadenceBox.SelectedItem = item;
        foreach (var radio in new[] { MinuteWindow, FiveMinuteWindow, HalfHourWindow, HourWindow })
            radio.IsChecked = radio.Tag?.ToString() == settings.WindowSeconds.ToString(CultureInfo.InvariantCulture);
        foreach (var radio in new[] { MinuteWindow, FiveMinuteWindow, HalfHourWindow, HourWindow })
            radio.IsCheckedChanged += WindowChanged;

        timer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
        timer.Tick += async (_, _) => await RefreshAsync();
    }

    TrayIcon? CreateTrayIcon()
    {
        try
        {
            var menu = new NativeMenu();
            var open = new NativeMenuItem("打开统计面板");
            open.Click += (_, _) => ShowPanel();
            var refresh = new NativeMenuItem("立即刷新");
            refresh.Click += async (_, _) => await RefreshAsync();
            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) => Close();
            menu.Items.Add(open);
            menu.Items.Add(refresh);
            menu.Items.Add(exit);

            var icon = new TrayIcon
            {
                Icon = TrayIconFactory.Create(),
                ToolTipText = "Codex TPS 悬浮窗",
                Menu = menu,
                IsVisible = true
            };
            // Let AppKit tint the transparent glyph for light/dark menu bars.
            MacOSProperties.SetIsTemplateIcon(icon, OperatingSystem.IsMacOS());
            icon.Clicked += (_, _) => TogglePanel();
            return icon;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task StartAsync()
    {
        if (initialized) return;
        initialized = true;
        timer.Start();
        await RefreshAsync();
    }

    public async Task CaptureAndCloseAsync(string path)
    {
        ShowPanel();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Capture(path);
            Close();
        }, DispatcherPriority.ApplicationIdle);
    }

    void PositionPanel()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        double scale = screen.Scaling;
        var area = screen.WorkingArea;
        double areaLeft = area.X / scale, areaTop = area.Y / scale;
        double areaRight = area.Right / scale, areaBottom = area.Bottom / scale;
        double x = settings.X ?? areaRight - Width - 24;
        double y = settings.Y ?? areaBottom - Bounds.Height - 24;
        Position = new PixelPoint(
            (int)Math.Round(Math.Clamp(x, areaLeft, Math.Max(areaLeft, areaRight - Width)) * scale),
            (int)Math.Round(Math.Clamp(y, areaTop, Math.Max(areaTop, areaBottom - Bounds.Height)) * scale));
        positioned = true;
    }

    public void ShowPanel()
    {
        Show();
        WindowState = WindowState.Normal;
        RenderHistory();
        Activate();
    }

    void TogglePanel()
    {
        if (IsVisible && HasTray) Hide();
        else ShowPanel();
    }

    async Task RefreshAsync()
    {
        if (closing) return;
        if (refreshing) { refreshPending = true; return; }
        refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "读取中";
        try
        {
            var activeScanner = scanner;
            var activeClocks = requestClocks;
            var next = await Task.Run(() =>
            {
                activeScanner.RequestStarts = activeClocks.Refresh();
                return activeScanner.Refresh(BeijingNow);
            });
            if (closing || !ReferenceEquals(activeScanner, scanner)) return;
            bool followToday = snapshot == null || DateBox.SelectedDate == snapshot.GeneratedAt.Date;
            snapshot = next;
            if (followToday) DateBox.SelectedDate = next.GeneratedAt.Date;

            string model = ModelBox.SelectedItem as string ?? "全部模型";
            var nextModels = new[] { "全部模型" }.Concat(next.Records.Select(r => r.Model).Distinct().Order()).ToList();
            if (!models.SequenceEqual(nextModels))
            {
                models.Clear();
                models.AddRange(nextModels);
                ModelBox.ItemsSource = null;
                ModelBox.ItemsSource = models;
                ModelBox.SelectedItem = models.Contains(model) ? model : "全部模型";
            }

            RenderMetrics();
            bool trouble = next.ReadFailures > 0 || next.InvalidLines > 0 || next.UnsupportedFiles > 0;
            string status = next.Files == 0 ? "未找到会话日志" : trouble ? "部分日志不可计量" : "就绪";
            StatusText.Text = $"{status} · {next.GeneratedAt:HH:mm:ss}";
            StatusDot.Fill = new SolidColorBrush(trouble || next.Files == 0
                ? Color.FromRgb(255, 149, 0)
                : Color.FromRgb(52, 199, 89));
            ToolTip.SetTip(StatusText,
                $"{next.Files} 个文件；{next.InvalidLines} 条异常记录；{next.UnsupportedFiles} 个旧格式文件；{next.ReadFailures} 个读取失败。\nJSONL 扫描 {next.ScanMilliseconds:F0} 毫秒。" +
                (activeClocks.Warning == null ? "" : "\n" + activeClocks.Warning));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "读取失败";
            StatusDot.Fill = Brushes.OrangeRed;
            ToolTip.SetTip(StatusText, e.Message);
        }
        finally
        {
            refreshing = false;
            RefreshButton.IsEnabled = true;
            if (refreshPending && !closing)
            {
                refreshPending = false;
                await RefreshAsync();
            }
        }
    }

    string? ModelFilter => ModelBox.SelectedItem is string value && value != "全部模型" ? value : null;

    static string Rate(double value) => value >= 1_000_000
        ? $"{value / 1_000_000:F1}m"
        : value >= 1000 ? $"{value / 1000:F1}k" : value.ToString("F1", CultureInfo.InvariantCulture);

    void RenderMetrics()
    {
        if (snapshot == null) return;
        var m = UsageScanner.Window(snapshot, settings.WindowSeconds, ModelFilter);
        HeroValue.Text = m.GenerationTps?.ToString("N1", CultureInfo.InvariantCulture) ?? "—";
        RequestValue.Text = m.RequestsPerMinute.ToString("F1", CultureInfo.InvariantCulture);
        InputValue.Text = Rate(m.InputRate);
        CachedValue.Text = Rate(m.CachedRate);
        OutputValue.Text = Rate(m.OutputRate);
        ReasoningValue.Text = Rate(m.ReasoningRate);
        SessionsValue.Text = $"{m.ActiveSessions} 个活跃会话";
        CacheRatioValue.Text = $"缓存占比 {m.CacheRatio:P0}";
        SamplesValue.Text = $"计时样本 {m.GenerationSamples}";
        TtftValue.Text = m.TurnFirstTokenMs.HasValue
            ? $"轮次首字 {m.TurnFirstTokenMs.Value / 1000:F1} 秒"
            : "轮次首字 —";
        if (tray != null) tray.ToolTipText = $"Codex TPS：{HeroValue.Text} 词元/秒（估算）";
        RenderHistory();
    }

    void RenderHistory()
    {
        if (snapshot == null || !IsVisible || !HistoryPanel.IsVisible) return;
        HourGrid.ItemsSource = UsageScanner.Hours(
            snapshot, (DateBox.SelectedDate ?? BeijingNow).Date, ModelFilter);
    }

    void SaveSettings()
    {
        if (capturePath != null) return;
        string? error = settingsStore.Save(settings);
        if (error != null && !closing) ToolTip.SetTip(StatusText, "设置保存失败：" + error);
    }

    void DragPanel(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && HasInteractiveAncestor(source)) return;
        BeginMoveDrag(e);
        settings.X = Position.X / (Screens.ScreenFromWindow(this)?.Scaling ?? 1);
        settings.Y = Position.Y / (Screens.ScreenFromWindow(this)?.Scaling ?? 1);
        SaveSettings();
    }

    // macOS three-finger drag and ordinary mouse drag both end up here, so the whole
    // panel is a drag surface except for controls that need the press themselves.
    static bool HasInteractiveAncestor(Visual source) =>
        source.FindAncestorOfType<Button>(includeSelf: true) != null
        || source.FindAncestorOfType<TextBox>(includeSelf: true) != null
        || source.FindAncestorOfType<ComboBox>(includeSelf: true) != null
        || source.FindAncestorOfType<DataGrid>(includeSelf: true) != null;

    async void RefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();

    void FolderClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(settings.CodexHome, "sessions");
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            _ = ShowMessage("未找到会话目录，请在设置中选择包含 sessions 的 Codex 数据目录。");
    }

    void PinClick(object? sender, RoutedEventArgs e)
    {
        settings.Pinned = !settings.Pinned;
        ApplyPinned();
    }

    void ApplyPinned()
    {
        Topmost = settings.Pinned;
        TopmostBox.IsChecked = settings.Pinned;
        SetPinColor(settings.Pinned);
        SaveSettings();
    }

    void SetPinColor(bool pinned) => PinButton.Foreground = new SolidColorBrush(
        pinned ? Color.FromRgb(0, 122, 255) : Colors.Gray);

    void TopmostClick(object? sender, RoutedEventArgs e)
    {
        settings.Pinned = TopmostBox.IsChecked == true;
        ApplyPinned();
    }

    void HideClick(object? sender, RoutedEventArgs e)
    {
        if (HasTray) Hide();
    }

    void ExitClick(object? sender, RoutedEventArgs e) => Close();

    void WindowChanged(object? sender, RoutedEventArgs e)
    {
        if (!initialized || sender is not RadioButton radio || radio.IsChecked != true) return;
        if (!int.TryParse(radio.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)) return;
        settings.WindowSeconds = seconds;
        RenderMetrics();
        SaveSettings();
    }

    void ModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initialized) RenderMetrics();
    }

    void DateChanged(object? sender, DatePickerSelectedValueChangedEventArgs e)
    {
        if (initialized) RenderHistory();
    }

    void CadenceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!initialized || CadenceBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)) return;
        settings.RefreshSeconds = seconds;
        timer.Interval = TimeSpan.FromSeconds(seconds);
        SaveSettings();
    }

    void HistoryClick(object? sender, RoutedEventArgs e)
    {
        bool show = !HistoryPanel.IsVisible;
        HistoryPanel.IsVisible = show;
        HistoryButton.Content = show ? "分时统计  ⌄" : "今日分时  ›";
        if (show)
        {
            RenderHistory();
            KeepOnScreen();
        }
    }

    void SettingsClick(object? sender, RoutedEventArgs e)
    {
        SettingsPanel.IsVisible = !SettingsPanel.IsVisible;
        KeepOnScreen();
    }

    void KeepOnScreen() => Dispatcher.UIThread.Post(() =>
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        double scale = screen.Scaling;
        double bottom = screen.WorkingArea.Bottom / scale;
        double top = Position.Y / scale;
        if (top + Bounds.Height > bottom)
            Position = new PixelPoint(Position.X, (int)Math.Round(Math.Max(screen.WorkingArea.Y / scale, bottom - Bounds.Height) * scale));
    }, DispatcherPriority.Loaded);

    async void ChooseHomeClick(object? sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含 sessions 的 Codex 数据目录",
            AllowMultiple = false
        });
        string? path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        if (!Directory.Exists(Path.Combine(path, "sessions")))
        {
            _ = ShowMessage("该目录不包含 sessions 文件夹。");
            return;
        }
        settings.CodexHome = path;
        HomeBox.Text = settings.CodexHome;
        scanner = new(settings.CodexHome);
        requestClocks = new(settings.CodexHome);
        SaveSettings();
        await RefreshAsync();
    }

    void StartupClick(object? sender, RoutedEventArgs e)
    {
        string? error = autostart.SetEnabled(StartupBox.IsChecked == true);
        if (error == null) return;
        StartupBox.IsChecked = autostart.IsEnabled();
        _ = ShowMessage("登录启动设置失败：" + error);
    }

    async void ExportClick(object? sender, RoutedEventArgs e)
    {
        if (snapshot == null) return;
        var date = (DateBox.SelectedDate ?? BeijingNow).Date;
        var markdownFileType = new FilePickerFileType("中文报告") { Patterns = new[] { "*.md" } };
        var jsonFileType = new FilePickerFileType("数据文件") { Patterns = new[] { "*.json" } };
        var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = "导出报告",
            SuggestedFileName = $"Codex-TPS-{date:yyyy-MM-dd}.md",
            DefaultExtension = "md",
            ShowOverwritePrompt = true,
            SuggestedFileType = markdownFileType,
            FileTypeChoices = new[] { markdownFileType, jsonFileType }
        });
        string? path = result.File?.TryGetLocalPath();
        if (path == null) return;
        string? selectedPattern = result.SelectedFileType?.Patterns?.FirstOrDefault();
        bool json = string.Equals(selectedPattern, "*.json", StringComparison.OrdinalIgnoreCase)
            || (selectedPattern == null && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase));
        string targetPath = Path.ChangeExtension(path, json ? ".json" : ".md");
        try
        {
            // The native picker confirmed only its returned path, before we changed the extension.
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(path, targetPath, comparison) && File.Exists(targetPath)
                && !await ShowMessage($"文件已存在：\n{targetPath}\n\n是否覆盖？", confirm: true)) return;
            var hours = UsageScanner.Hours(snapshot, date, ModelFilter);
            ReportExporter.Write(targetPath, snapshot, date, ModelFilter, hours);
            _ = ShowMessage("报告已保存。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessage("导出失败：" + ex.Message);
        }
    }

    async Task<bool> ShowMessage(string message, bool confirm = false)
    {
        var accept = new Button { Content = confirm ? "覆盖" : "确定", IsDefault = !confirm };
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        var dialog = new Window
        {
            Title = "Codex TPS",
            Width = 330,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    actions
                }
            }
        };
        if (confirm)
        {
            var cancel = new Button { Content = "取消", IsDefault = true, IsCancel = true };
            cancel.Click += (_, _) => dialog.Close(false);
            actions.Children.Add(cancel);
        }
        accept.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(accept);
        return await dialog.ShowDialog<bool>(this);
    }

    void Capture(string path)
    {
        UpdateLayout();
        double scale = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
        bitmap.Save(path);
    }

    void DisposeWindow()
    {
        if (closing) return;
        closing = true;
        timer.Stop();
        if (positioned && double.IsFinite(Position.X) && double.IsFinite(Position.Y))
        {
            double scale = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
            settings.X = Position.X / scale;
            settings.Y = Position.Y / scale;
        }
        SaveSettings();
        tray?.Dispose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}

static class TrayIconFactory
{
    public static WindowIcon Create()
    {
        // An 18-point glyph rendered at 2x stays sharp on Retina displays.
        // Leave transparent padding so the menu bar shows bars, not a solid square.
        using var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(192, 192));
        using (var drawing = bitmap.CreateDrawingContext())
        {
            IBrush brush = OperatingSystem.IsMacOS() ? Brushes.Black : new SolidColorBrush(Color.Parse("#007AFF"));
            drawing.DrawRectangle(brush, null, new Rect(2, 10, 3, 6), 0.75, 0.75);
            drawing.DrawRectangle(brush, null, new Rect(7.5, 6, 3, 10), 0.75, 0.75);
            drawing.DrawRectangle(brush, null, new Rect(13, 2, 3, 14), 0.75, 0.75);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
