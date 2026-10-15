using System.Text;
using System.Text.Json;
using CodexTpsFloat;

static string Event(string type, object payload, double second) => JsonSerializer.Serialize(new
{
    type, timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(second * 1000)).ToString("O"), payload
});
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(double? actual, double expected, string message) => Check(actual.HasValue && Math.Abs(actual.Value - expected) < .001, message + $"：{actual}");
const double origin = 1791907200; // 2026-10-14 00:00 UTC+8.
static void Seed(UsageScanner s, double t = origin)
{
    s.Ingest(Event("event_msg", new { type = "task_started", turn_id = "turn-a" }, t));
    s.Ingest(Event("turn_context", new { turn_id = "turn-a", model = "model-a" }, t));
    s.Ingest(Event("event_msg", new { type = "item_completed", turn_id = "turn-a", started_at_ms = (t + 2) * 1000,
        completed_at_ms = (t + 6) * 1000, item = new { type = "Reasoning", id = "item-a" } }, t + 6));
    s.Ingest(Event("token_usage_record", new { turn_id = "turn-a", thread_id = "thread-a", response_id = "response-a",
        usage = new { input_tokens = 1000, cached_input_tokens = 800, output_tokens = 100, reasoning_output_tokens = 60 } }, t + 7));
}
var scanner = new UsageScanner(".");
Seed(scanner); Seed(scanner);
var rows = scanner.TimedRecords();
Check(rows.Count == 1, "重复 response_id 去重"); Near(rows[0].Tps, 20, "输出包含推理但不重复加推理");
Check(rows[0].Usage.Output == 100 && rows[0].Usage.Input == 1000, "输入不参与生成 TPS");
var now = DateTimeOffset.FromUnixTimeSeconds((long)origin + 60).ToOffset(TimeSpan.FromHours(8));
var snap = new UsageSnapshot(now, rows, new Dictionary<string, TurnData>(), 1, 0, 0, 0, 0);
var stats = UsageScanner.Window(snap, 60);
Near(stats.InputRate, 1000d / 60, "窗口吞吐分母使用完整窗口"); Near(stats.OutputRate, 100d / 60, "输出吞吐和生成速度不同");
Near(stats.GenerationTps, 20, "生成速度按持续时间加权"); Near(stats.CacheRatio, .8, "缓存是输入子集");
Near(stats.RequestsPerMinute, 1, "完成请求计数"); Check(stats.ActiveSessions == 1, "活跃会话去重");
Check(UsageScanner.Window(snap, 5).GenerationTps == null, "空窗口不能显示 0 生成速度");
var hours = UsageScanner.Hours(snap, now.Date);
Check(hours[0].Samples == 1 && hours[1].Samples == 0 && hours[1].Median == null, "北京时间小时与缺样本");
Near(hours[0].Median, 20, "小时中位数");

var zero = new UsageScanner(".");
zero.Ingest(Event("event_msg", new { type = "task_started", turn_id = "z" }, origin));
zero.Ingest(Event("event_msg", new { type = "item_completed", turn_id = "z", started_at_ms = (origin + 1) * 1000,
    completed_at_ms = (origin + 1) * 1000, item = new { type = "Reasoning", id = "z" } }, origin + 1));
zero.Ingest(Event("token_usage_record", new { turn_id = "z", thread_id = "z", response_id = "z", usage = new { output_tokens = 100 } }, origin + 1.1));
Check(zero.TimedRecords()[0].Tps == null, "零持续时间不产生虚高 TPS");
var concurrent = new UsageScanner("."); Seed(concurrent);
concurrent.Ingest(Event("event_msg", new { type = "task_started", turn_id = "b" }, origin));
concurrent.Ingest(Event("turn_context", new { turn_id = "b", model = "model-b" }, origin));
concurrent.Ingest(Event("event_msg", new { type = "item_completed", turn_id = "b", started_at_ms = (origin + 2) * 1000,
    completed_at_ms = (origin + 6) * 1000, item = new { type = "AgentMessage", id = "b" } }, origin + 6));
concurrent.Ingest(Event("token_usage_record", new { turn_id = "b", thread_id = "thread-b", response_id = "b", usage = new { output_tokens = 200 } }, origin + 7));
var both = new UsageSnapshot(now, concurrent.TimedRecords(), new Dictionary<string, TurnData>(), 1, 0, 0, 0, 0);
Near(UsageScanner.Window(both, 60).GenerationTps, 30, "并发耗时分别相加");
Near(UsageScanner.Window(both, 60, "model-b").GenerationTps, 40, "模型筛选");
var retry = new UsageScanner("."); Seed(retry);
retry.RequestStarts = new Dictionary<string, double[]> { ["thread-a/turn-a"] = new[] { origin + 3 } };
Check(retry.TimedRecords()[0].Tps == null, "不把失败尝试的输出项计入后续请求的计时");

var directory = Path.Combine(Path.GetTempPath(), "codex-tps-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(directory, "sessions"));
Directory.CreateDirectory(Path.Combine(directory, "archived_sessions"));
try
{
    string file = Path.Combine(directory, "sessions", "test.jsonl");
    string row = Event("token_usage_record", new { turn_id = "partial", response_id = "partial", thread_id = "p", usage = new { output_tokens = 10 } }, origin + 4);
    File.WriteAllText(file, row[..(row.Length / 2)], new UTF8Encoding(true));
    var inc = new UsageScanner(directory);
    Check(inc.Refresh(now).Records.Count == 0, "未完成尾行不计数");
    File.AppendAllText(file, row[(row.Length / 2)..] + "\n", new UTF8Encoding(false));
    var initial = inc.Refresh(now);
    Check(initial.Records.Count == 1 && initial.InvalidLines == 0, "UTF8 BOM 与尾行补全");
    Check(inc.Refresh(now).Records.Count == 1, "重复刷新不重复计数");
    File.Move(file, Path.Combine(directory, "archived_sessions", "test.jsonl"));
    Check(inc.Refresh(now).Records.Count == 1, "归档移动不重复计数");
    File.WriteAllText(Path.Combine(directory, "archived_sessions", "test.jsonl"), "{}\n", Encoding.UTF8);
    Check(inc.Refresh(now).Records.Count == 0, "截断文件清除陈旧统计");
    File.WriteAllText(file, "bad-json\n" + row + "\n", Encoding.UTF8);
    var partial = inc.Refresh(now);
    Check(partial.InvalidLines == 1 && partial.Records.Count == 1, "坏记录不阻断有效记录");

    // A rewrite that keeps the length identical must still be detected; on Unix systems
    // birth time is unavailable, so identity has to come from the consumed bytes.
    string replacedRoot = Path.Combine(directory, "replaced");
    Directory.CreateDirectory(Path.Combine(replacedRoot, "sessions"));
    string replacedFile = Path.Combine(replacedRoot, "sessions", "same-length.jsonl");
    File.WriteAllText(replacedFile, row + "\n", new UTF8Encoding(false));
    var replaced = new UsageScanner(replacedRoot);
    Check(replaced.Refresh(now).Records.Count == 1, "重写前计入一条");
    string other = row.Replace("\"response_id\":\"partial\"", "\"response_id\":\"rewrite\"");
    Check(other.Length == row.Length, "重写样本保持等长");
    File.WriteAllText(replacedFile, other + "\n", new UTF8Encoding(false));
    var afterReplace = replaced.Refresh(now);
    Check(afterReplace.Records.Count == 1 && afterReplace.Records[0].Usage.Id == "rewrite",
        "等长重写后重建游标并采用新内容");

    string streamRoot = Path.Combine(directory, "streaming");
    Directory.CreateDirectory(Path.Combine(streamRoot, "sessions"));
    string streamFile = Path.Combine(streamRoot, "sessions", "unicode.jsonl");
    string longLine = "{\"type\":\"response_item\",\"payload\":{\"text\":\"" + new string('中', 70000) + "\"}}\r\n";
    string unicodeRow = row.Replace("\"thread_id\":\"p\"", "\"thread_id\":\"中文线程\"");
    byte[] unicodeBytes = Encoding.UTF8.GetBytes(unicodeRow + "\r\n");
    int middleOfCharacter = Encoding.UTF8.GetByteCount(unicodeRow[..unicodeRow.IndexOf("中文线程", StringComparison.Ordinal)]) + 1;
    File.WriteAllText(streamFile, longLine + " \t \r\n", new UTF8Encoding(true));
    using (var output = new FileStream(streamFile, FileMode.Append)) output.Write(unicodeBytes, 0, middleOfCharacter);
    var streamed = new UsageScanner(streamRoot);
    var unfinished = streamed.Refresh(now);
    Check(unfinished.Records.Count == 0 && unfinished.InvalidLines == 0, "跨缓冲区长行、空白 CRLF 和未完成 UTF8 尾行");
    using (var output = new FileStream(streamFile, FileMode.Append))
        output.Write(unicodeBytes, middleOfCharacter, unicodeBytes.Length - middleOfCharacter);
    var completed = streamed.Refresh(now);
    Check(completed.Records.Count == 1 && completed.InvalidLines == 0
        && completed.Records[0].Usage.Thread == "中文线程", "从中文字符中间补全尾行且按字节推进");
    string secondRow = unicodeRow.Replace("\"response_id\":\"partial\"", "\"response_id\":\"second\"");
    File.AppendAllText(streamFile, secondRow + "\n", new UTF8Encoding(false));
    Check(streamed.Refresh(now).Records.Count == 2 && streamed.Refresh(now).InvalidLines == 0,
        "长行后继续增量读取，CRLF 和 LF 不重复或遗漏记录");

    string legacyRoot = Path.Combine(directory, "legacy");
    Directory.CreateDirectory(Path.Combine(legacyRoot, "sessions"));
    string legacyFile = Path.Combine(legacyRoot, "sessions", "aborted.jsonl");
    File.WriteAllText(legacyFile, string.Join("\n",
        Event("event_msg", new { type = "task_started", turn_id = "aborted" }, origin),
        Event("event_msg", new { type = "item_completed", turn_id = "aborted",
            item = new { type = "UserMessage", id = "user-item" } }, origin + 1),
        Event("event_msg", new { type = "token_count", info = (object?)null }, origin + 2),
        Event("event_msg", new { type = "turn_aborted", turn_id = "aborted" }, origin + 3)) + "\n", new UTF8Encoding(false));
    var legacy = new UsageScanner(legacyRoot);
    var aborted = legacy.Refresh(now);
    Check(aborted.UnsupportedFiles == 0 && aborted.Records.Count == 0 && aborted.InvalidLines == 0,
        "仅有空 token_count 的中断会话不触发旧格式告警");
    File.AppendAllText(legacyFile,
        Event("event_msg", new { type = "token_count" }, origin + 4) + "\n" +
        Event("event_msg", new { type = "token_count", info = new { } }, origin + 5) + "\n" +
        Event("event_msg", new { type = "token_count", info = new {
            total_token_usage = new { input_tokens = 100, output_tokens = 0 },
            last_token_usage = new { input_tokens = 100, output_tokens = 0 } } }, origin + 6) + "\n", new UTF8Encoding(false));
    Check(legacy.Refresh(now).UnsupportedFiles == 0, "缺失或零输出用量不触发旧格式告警");
    File.AppendAllText(legacyFile, Event("event_msg", new { type = "token_count",
        info = new { total_token_usage = new { output_tokens = 10 } } }, origin + 7) + "\n", new UTF8Encoding(false));
    var actualLegacy = legacy.Refresh(now);
    Check(actualLegacy.UnsupportedFiles == 1 && actualLegacy.Records.Count == 0,
        "增量读到真实旧格式输出用量后仍告警，不虚构响应样本");
    File.AppendAllText(legacyFile, Event("event_msg", new { type = "token_count", info = (object?)null }, origin + 8) + "\n",
        new UTF8Encoding(false));
    Check(legacy.Refresh(now).UnsupportedFiles == 1, "后续空用量事件不清除已有旧格式告警");
    File.WriteAllText(Path.Combine(legacyRoot, "sessions", "last-usage.jsonl"), Event("event_msg",
        new { type = "token_count", info = new { last_token_usage = new { output_tokens = 5 } } }, origin + 9) + "\n",
        new UTF8Encoding(false));
    Check(legacy.Refresh(now).UnsupportedFiles == 2, "逐次旧格式输出用量也触发告警");
    File.AppendAllText(legacyFile, row + "\n", new UTF8Encoding(false));
    var modern = legacy.Refresh(now);
    Check(modern.UnsupportedFiles == 1 && modern.Records.Count == 1 && modern.InvalidLines == 0,
        "同文件出现逐次 token_usage_record 后恢复可计量状态");

    string settingsDirectory = Path.Combine(directory, "settings");
    var store = new SettingsStore(Path.Combine(settingsDirectory, "settings.json"));
    Check(store.Load().WindowSeconds == 300, "缺失设置文件时回退到默认值");
    var custom = new PanelSettings { CodexHome = directory, WindowSeconds = 999, RefreshSeconds = 42,
        Pinned = false, X = 12.5, Y = 34.5 };
    store.Save(custom);
    var loaded = store.Load();
    Check(loaded.WindowSeconds == 300 && loaded.RefreshSeconds == 15, "越界选项在读取时归一化");
    Check(loaded.Pinned == false && loaded.X == 12.5 && loaded.Y == 34.5 && loaded.CodexHome == directory,
        "有效设置项完整往返");
    Check(!File.Exists(store.Path + ".tmp"), "原子保存不残留临时文件");

    var exportHours = new[] { new HourStats("09:00–10:00", 2, 21.5, 22.25), new HourStats("10:00–11:00", 0, null, null) };
    string markdownPath = Path.Combine(directory, "report.md");
    ReportExporter.Write(markdownPath, snap, now.Date, "model-a", exportHours);
    string markdown = File.ReadAllText(markdownPath);
    Check(markdown.Contains("| 09:00–10:00 | 2 | 21.50 | 22.25 |") && markdown.Contains("| 10:00–11:00 | 0 | — | — |"),
        "Markdown 报告包含有效与空样本行");
    string jsonPath = Path.Combine(directory, "report.json");
    ReportExporter.Write(jsonPath, snap, now.Date, null, exportHours);
    using var reportJson = JsonDocument.Parse(File.ReadAllText(jsonPath));
    Check(reportJson.RootElement.GetProperty("timezone").GetString() == "UTC+8"
        && reportJson.RootElement.GetProperty("model").GetString() == "全部模型"
        && reportJson.RootElement.GetProperty("hours").GetArrayLength() == 2, "JSON 报告字段与导出参数一致");

    string entry = OperatingSystem.IsMacOS()
        ? Path.Combine(directory, "LaunchAgents", "com.codextpsfloat.plist")
        : Path.Combine(directory, "config", "autostart", "CodexTpsFloat.desktop");
    IAutostartService login = OperatingSystem.IsMacOS()
        ? new MacAutostart("/Applications/Codex TPS.app", Path.GetDirectoryName(entry))
        : new LinuxAutostart("/opt/codex tps/CodexTpsFloat", Path.Combine(directory, "config"));
    Check(!login.IsEnabled(), "未启用时登录启动为关闭状态");
    Check(login.SetEnabled(true) == null && login.IsEnabled(), "启用登录启动会创建条目");
    string entryText = File.ReadAllText(entry);
    Check(OperatingSystem.IsMacOS()
        ? entryText.Contains("<string>/Applications/Codex TPS.app</string>") && entryText.Contains("<key>RunAtLoad</key>")
        : entryText.Contains("Exec=\"/opt/codex tps/CodexTpsFloat\"") && entryText.StartsWith("[Desktop Entry]"),
        "登录启动条目包含可执行路径与自启标记");
    Check(login.SetEnabled(false) == null && !login.IsEnabled() && !File.Exists(entry), "停用登录启动会移除条目");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("通过：统计口径、去重、重试区间、时区、增量刷新、归档、截断、等长重写、坏记录、BOM、跨缓冲区长行、CRLF、中文字符中间补全尾行、旧格式告警、设置往返、报告导出与登录启动。");
