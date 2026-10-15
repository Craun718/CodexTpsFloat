using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexTpsFloat;

public partial class MainWindow : Window
{
    readonly DispatcherTimer timer = new();
    readonly Forms.NotifyIcon tray;
    readonly IAutostartService autostart = Autostart.Create();
    readonly SettingsStore settingsStore = OperatingSystem.IsWindows()
        ? SettingsStore.ForPortableApp(AppContext.BaseDirectory)
        : SettingsStore.ForUserProfile();
    readonly string? capturePath;
    PanelSettings settings;
    UsageScanner scanner;
    RequestClockReader requestClocks;
    UsageSnapshot? snapshot;
    bool refreshing, initialized, closing;
    public MainWindow(string? capturePath = null, string? codexHome = null)
    {
        this.capturePath = capturePath;
        settings = settingsStore.Load();
        if (codexHome != null) settings.CodexHome = Path.GetFullPath(codexHome);
        settings.Normalize();
        scanner = new(settings.CodexHome);
        requestClocks = new(settings.CodexHome);
        InitializeComponent();
        HomeBox.Text = settings.CodexHome;
        Topmost = settings.Pinned;
        TopmostBox.IsChecked = settings.Pinned;
        PinButton.Foreground = new SolidColorBrush(settings.Pinned ? Color.FromRgb(0, 122, 255) : Colors.Gray);
        DateBox.SelectedDate = BeijingNow.Date;
        StartupBox.IsChecked = autostart.IsEnabled();
        foreach (var item in CadenceBox.Items.OfType<ComboBoxItem>())
            item.IsSelected = item.Tag?.ToString() == settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        foreach (var radio in new[] { MinuteWindow, FiveMinuteWindow, HalfHourWindow, HourWindow })
            if (radio.Tag?.ToString() == settings.WindowSeconds.ToString(CultureInfo.InvariantCulture)) radio.IsChecked = true;
        timer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
        timer.Tick += async (_, _) => await RefreshAsync();
        tray = new Forms.NotifyIcon { Text = "Codex TPS 悬浮窗", Visible = true, Icon = MakeTrayIcon() };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开统计面板", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        menu.Items.Add("立即刷新", null, async (_, _) => await Dispatcher.InvokeAsync(RefreshAsync).Task.Unwrap());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(() => { if (IsVisible) Hide(); else ShowPanel(); });
        };
        Loaded += (_, _) => PositionPanel();
        Closing += (_, _) =>
        {
            closing = true; timer.Stop();
            if (double.IsFinite(Left) && double.IsFinite(Top))
            { settings.X = Left; settings.Y = Top; }
            SaveSettings();
            var icon = tray.Icon; tray.Visible = false; tray.Dispose(); icon?.Dispose();
        };
        Closed += (_, _) => Application.Current.Shutdown();
    }
    static DateTimeOffset BeijingNow => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
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
        await Dispatcher.InvokeAsync(() => { Capture(path); Close(); }, DispatcherPriority.ApplicationIdle);
    }
    static System.Drawing.Icon MakeTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.FromArgb(0, 122, 255));
        using var font = new System.Drawing.Font("Segoe UI", 15, System.Drawing.FontStyle.Bold);
        g.DrawString("T", font, System.Drawing.Brushes.White, 4, 2);
        var handle = bitmap.GetHicon();
        try { using var temp = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)temp.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
    void PositionPanel()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(settings.X ?? area.Right - ActualWidth - 24, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(settings.Y ?? area.Bottom - ActualHeight - 24, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }
    public void ShowPanel() { Show(); WindowState = WindowState.Normal; RenderHistory(); Activate(); }
    async Task RefreshAsync()
    {
        if (refreshing || closing) return;
        refreshing = true; RefreshButton.IsEnabled = false;
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
            if (closing) return;
            bool followToday = snapshot == null || DateBox.SelectedDate == snapshot.GeneratedAt.Date;
            snapshot = next;
            if (followToday) DateBox.SelectedDate = next.GeneratedAt.Date;
            string model = ModelBox.SelectedItem as string ?? "全部模型";
            var models = new[] { "全部模型" }.Concat(next.Records.Select(r => r.Model).Distinct().Order()).ToArray();
            if (!ModelBox.Items.Cast<string>().SequenceEqual(models))
            {
                ModelBox.ItemsSource = models;
                ModelBox.SelectedItem = models.Contains(model) ? model : "全部模型";
            }
            RenderMetrics();
            bool trouble = next.ReadFailures > 0 || next.InvalidLines > 0 || next.UnsupportedFiles > 0;
            string status = next.Files == 0 ? "未找到会话日志" : trouble ? "部分日志不可计量" : "就绪";
            StatusText.Text = $"{status} · {next.GeneratedAt:HH:mm:ss}";
            StatusDot.Fill = new SolidColorBrush(trouble || next.Files == 0 ? Color.FromRgb(255, 149, 0) : Color.FromRgb(52, 199, 89));
            StatusText.ToolTip = $"{next.Files} 个文件；{next.InvalidLines} 条异常记录；{next.UnsupportedFiles} 个旧格式文件；{next.ReadFailures} 个读取失败。\nJSONL 扫描 {next.ScanMilliseconds:F0} 毫秒。" + (activeClocks.Warning == null ? "" : "\n" + activeClocks.Warning);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "读取失败";
            StatusDot.Fill = Brushes.OrangeRed;
            StatusText.ToolTip = e.Message;
        }
        finally { refreshing = false; RefreshButton.IsEnabled = true; }
    }
    string? ModelFilter => ModelBox.SelectedItem is string value && value != "全部模型" ? value : null;
    static string Rate(double value) => value >= 1_000_000 ? $"{value / 1_000_000:F1}m" : value >= 1000 ? $"{value / 1000:F1}k" : value.ToString("F1", CultureInfo.InvariantCulture);
    void RenderMetrics()
    {
        if (snapshot == null) return;
        var m = UsageScanner.Window(snapshot, settings.WindowSeconds, ModelFilter);
        HeroValue.Text = m.GenerationTps?.ToString("N1", CultureInfo.InvariantCulture) ?? "—";
        RequestValue.Text = m.RequestsPerMinute.ToString("F1", CultureInfo.InvariantCulture);
        InputValue.Text = Rate(m.InputRate); CachedValue.Text = Rate(m.CachedRate);
        OutputValue.Text = Rate(m.OutputRate); ReasoningValue.Text = Rate(m.ReasoningRate);
        SessionsValue.Text = $"{m.ActiveSessions} 个活跃会话";
        CacheRatioValue.Text = $"缓存占比 {m.CacheRatio:P0}";
        SamplesValue.Text = $"计时样本 {m.GenerationSamples}";
        TtftValue.Text = m.TurnFirstTokenMs.HasValue ? $"轮次首字 {m.TurnFirstTokenMs.Value / 1000:F1} 秒" : "轮次首字 —";
        tray.Text = $"Codex TPS：{HeroValue.Text} 词元/秒（估算）";
        RenderHistory();
    }
    void RenderHistory()
    {
        if (snapshot == null || !IsVisible || HistoryPanel.Visibility != Visibility.Visible) return;
        HourGrid.ItemsSource = UsageScanner.Hours(snapshot, DateBox.SelectedDate ?? BeijingNow.Date, ModelFilter);
    }
    void SaveSettings()
    {
        string? error = settingsStore.Save(settings);
        if (error != null && !closing) StatusText.ToolTip = "设置保存失败：" + error;
    }
    void DragHeader(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? target = e.OriginalSource as DependencyObject;
        while (target != null)
        {
            if (target is Button) return;
            target = VisualTreeHelper.GetParent(target);
        }
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); settings.X = Left; settings.Y = Top; SaveSettings(); }
    }
    async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    void FolderClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(settings.CodexHome, "sessions");
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show(this, "未找到会话目录，请在设置中选择包含 sessions 的 Codex 数据目录。", "Codex TPS");
    }
    void PinClick(object sender, RoutedEventArgs e) { settings.Pinned = !settings.Pinned; ApplyPinned(); }
    void ApplyPinned()
    {
        Topmost = settings.Pinned; TopmostBox.IsChecked = settings.Pinned;
        PinButton.Foreground = new SolidColorBrush(settings.Pinned ? Color.FromRgb(0, 122, 255) : Colors.Gray); SaveSettings();
    }
    void TopmostClick(object sender, RoutedEventArgs e) { settings.Pinned = TopmostBox.IsChecked == true; ApplyPinned(); }
    void HideClick(object sender, RoutedEventArgs e) => Hide();
    void ExitClick(object sender, RoutedEventArgs e) => Close();
    void WindowChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        settings.WindowSeconds = int.Parse(((RadioButton)sender).Tag.ToString()!, CultureInfo.InvariantCulture);
        RenderMetrics(); SaveSettings();
    }
    void ModelChanged(object sender, SelectionChangedEventArgs e) { if (initialized) RenderMetrics(); }
    void DateChanged(object sender, SelectionChangedEventArgs e) { if (initialized) RenderHistory(); }
    void CadenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || CadenceBox.SelectedItem is not ComboBoxItem item) return;
        settings.RefreshSeconds = int.Parse(item.Tag.ToString()!, CultureInfo.InvariantCulture);
        timer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds); SaveSettings();
    }
    void HistoryClick(object sender, RoutedEventArgs e)
    {
        bool show = HistoryPanel.Visibility != Visibility.Visible;
        HistoryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HistoryButton.Content = show ? "分时统计  ⌄" : "今日分时  ›";
        if (show) { RenderHistory(); KeepOnScreen(); }
    }
    void SettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        KeepOnScreen();
    }
    void KeepOnScreen() => Dispatcher.InvokeAsync(() =>
    {
        var area = SystemParameters.WorkArea;
        if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }, DispatcherPriority.Loaded);
    async void ChooseHomeClick(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择包含 sessions 的 Codex 数据目录", SelectedPath = settings.CodexHome, UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (!Directory.Exists(Path.Combine(dialog.SelectedPath, "sessions")))
        { MessageBox.Show(this, "该目录不包含 sessions 文件夹。", "Codex TPS"); return; }
        settings.CodexHome = dialog.SelectedPath; HomeBox.Text = settings.CodexHome;
        scanner = new(settings.CodexHome); requestClocks = new(settings.CodexHome); SaveSettings(); await RefreshAsync();
    }
    void StartupClick(object sender, RoutedEventArgs e)
    {
        string? error = autostart.SetEnabled(StartupBox.IsChecked == true);
        if (error == null) return;
        StartupBox.IsChecked = autostart.IsEnabled();
        MessageBox.Show(this, "登录启动设置失败：" + error, "Codex TPS");
    }
    void ExportClick(object sender, RoutedEventArgs e)
    {
        if (snapshot == null) return;
        var date = DateBox.SelectedDate ?? BeijingNow.Date;
        var dialog = new SaveFileDialog { Filter = "中文报告 (*.md)|*.md|数据文件 (*.json)|*.json", FileName = $"Codex-TPS-{date:yyyy-MM-dd}.md" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var hours = UsageScanner.Hours(snapshot, date, ModelFilter);
            ReportExporter.Write(dialog.FileName, snapshot, date, ModelFilter, hours);
            MessageBox.Show(this, "报告已保存。", "Codex TPS");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "导出失败：" + ex.Message, "Codex TPS"); }
    }
    void Capture(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var image = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
}
