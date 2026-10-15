using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CodexTpsFloat;

public sealed record ModelItem(string Id, double Start, double End);
public sealed class TurnData
{
    public double? Start { get; set; }
    public double? FirstTokenMs { get; set; }
    public double? Completed { get; set; }
    public string Model { get; set; } = "未知模型";
    public Dictionary<string, ModelItem> Items { get; } = new();
}
public sealed record UsageRecord(string Id, string Thread, string Turn, double Completed,
    long Input, long Cached, long Output, long Reasoning);
public sealed record TimedUsage(UsageRecord Usage, string Model, double? StreamStart, double? StreamSeconds)
{
    public double? Tps => StreamSeconds is > 0 ? Usage.Output / StreamSeconds : null;
}
public sealed record WindowStats(int Seconds, double? GenerationTps, int GenerationSamples,
    double InputRate, double CachedRate, double OutputRate, double ReasoningRate,
    double RequestsPerMinute, int ActiveSessions, double CacheRatio, double? TurnFirstTokenMs);
public sealed record HourStats(string Hour, int Samples, double? Median, double? Weighted);
public sealed record UsageSnapshot(DateTimeOffset GeneratedAt, IReadOnlyList<TimedUsage> Records,
    IReadOnlyDictionary<string, TurnData> Turns, int Files, int InvalidLines, int UnsupportedFiles,
    int ReadFailures, double ScanMilliseconds);

public sealed class UsageScanner
{
    sealed class Cursor
    {
        public long Offset;
        public long Creation;
        public string Thread = "";
        public string Turn = "";
        public bool HasUsage;
        public bool HasLegacyUsage;
    }
    readonly Dictionary<string, Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, UsageRecord> usage = new();
    readonly Dictionary<string, TurnData> turns = new();
    readonly string home;
    int invalidLines;
    public IReadOnlyDictionary<string, double[]> RequestStarts { get; set; } = new Dictionary<string, double[]>();
    public UsageScanner(string codexHome) => home = Path.GetFullPath(codexHome);
    static string Str(JsonElement e, string k, string fallback = "") =>
        e.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;
    static double? Num(JsonElement e, string k) =>
        e.TryGetProperty(k, out var p) && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    static long Count(JsonElement e, string k) =>
        e.TryGetProperty(k, out var p) && p.TryGetInt64(out var n) && n >= 0 ? n : 0;
    static bool HasOutputUsage(JsonElement info, string key) =>
        info.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.Object && Count(u, "output_tokens") > 0;
    TurnData Turn(string id)
    {
        if (!turns.TryGetValue(id, out var turn)) turns[id] = turn = new();
        return turn;
    }
    public void Ingest(string line, string sourceThread = "")
    {
        var cursor = new Cursor { Thread = sourceThread };
        Parse(Encoding.UTF8.GetBytes(line), cursor);
    }
    void Parse(ReadOnlyMemory<byte> line, Cursor cursor)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return;
        string type = Str(root, "type");
        if (type == "session_meta") { cursor.Thread = Str(p, "id", Str(p, "session_id")); return; }
        if (!DateTimeOffset.TryParse(Str(root, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date)) return;
        double timestamp = date.ToUnixTimeMilliseconds() / 1000d;
        string turnId = Str(p, "turn_id", cursor.Turn);
        if (type == "turn_context")
        {
            cursor.Turn = turnId;
            if (turnId.Length > 0) Turn(turnId).Model = Str(p, "model", "未知模型");
            return;
        }
        if (type == "event_msg")
        {
            string eventType = Str(p, "type");
            if (eventType == "token_count")
            {
                // Rate-limit-only updates do not imply a generated response.
                if (p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                    && (HasOutputUsage(info, "total_token_usage") || HasOutputUsage(info, "last_token_usage")))
                    cursor.HasLegacyUsage = true;
                return;
            }
            if (turnId.Length == 0) return;
            var turn = Turn(turnId);
            if (eventType == "task_started")
            {
                cursor.Turn = turnId;
                turn.Start = timestamp;
            }
            else if (eventType == "task_complete")
            {
                turn.FirstTokenMs = Num(p, "time_to_first_token_ms");
                turn.Completed = timestamp;
            }
            else if (eventType == "item_completed" && p.TryGetProperty("item", out var item))
            {
                string itemType = Str(item, "type");
                if (itemType is not ("AgentMessage" or "Reasoning")) return;
                double? start = Num(p, "started_at_ms");
                double? end = Num(p, "completed_at_ms");
                string id = Str(item, "id");
                if (start.HasValue && end.HasValue && end >= start && id.Length > 0)
                    turn.Items[id] = new(id, start.Value / 1000, end.Value / 1000);
            }
            return;
        }
        if (type != "token_usage_record") return;
        cursor.HasUsage = true;
        if (!p.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        string responseId = Str(p, "response_id");
        if (responseId.Length == 0 || turnId.Length == 0 || !u.TryGetProperty("output_tokens", out var output)
            || !output.TryGetInt64(out var outputCount) || outputCount < 0) { invalidLines++; return; }
        long input = Count(u, "input_tokens");
        long cached = Count(u, "cached_input_tokens");
        long reasoning = Count(u, "reasoning_output_tokens");
        if (cached > input || reasoning > outputCount) { invalidLines++; return; }
        usage[responseId] = new(responseId, Str(p, "thread_id", cursor.Thread), turnId, timestamp,
            input, cached, outputCount, reasoning);
    }
    void ReadCompleteLines(FileStream stream, Cursor cursor)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int buffered = 0;
        try
        {
            int read;
            while ((read = stream.Read(buffer, buffered, buffer.Length - buffered)) > 0)
            {
                buffered += read;
                int consumed = 0;
                while (consumed < buffered)
                {
                    int newline = buffer.AsSpan(consumed, buffered - consumed).IndexOf((byte)'\n');
                    if (newline < 0) break;
                    var line = buffer.AsMemory(consumed, newline);
                    if (!line.Span.Trim(" \t\r\v\f"u8).IsEmpty)
                    {
                        try { Parse(line, cursor); }
                        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
                        { invalidLines++; }
                    }
                    consumed += newline + 1;
                    cursor.Offset += newline + 1; // Byte offsets also cover CRLF and multibyte UTF-8.
                }
                buffered -= consumed;
                if (buffered > 0 && consumed > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered);
                if (buffered == buffer.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                    Buffer.BlockCopy(buffer, 0, larger, 0, buffered);
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    buffer = larger;
                }
            }
            // Leave an unfinished tail at its original offset for the next refresh.
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    public UsageSnapshot Refresh(DateTimeOffset now)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var files = new List<string>();
        int failures = 0;
        foreach (string directory in new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") })
        {
            if (!Directory.Exists(directory)) continue;
            try { files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; }
        }
        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (!cursors.TryGetValue(path, out var cursor)) cursors[path] = cursor = new();
                long creation = info.CreationTimeUtc.Ticks;
                if (info.Length < cursor.Offset || (cursor.Creation != 0 && cursor.Creation != creation))
                {
                    // Rebuild after replacement/truncation; stale usage must not survive a reset.
                    cursors.Clear(); usage.Clear(); turns.Clear(); invalidLines = 0;
                    return Refresh(now);
                }
                cursor.Creation = creation;
                if (info.Length == cursor.Offset) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (cursor.Offset == 0 && stream.Length >= 3)
                {
                    if (stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF)
                        cursor.Offset = 3;
                }
                stream.Seek(cursor.Offset, SeekOrigin.Begin);
                ReadCompleteLines(stream, cursor);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; }
        }
        var records = TimedRecords(now.ToUnixTimeMilliseconds() / 1000d);
        watch.Stop();
        var frozenTurns = turns.ToDictionary(pair => pair.Key, pair => new TurnData
        {
            Start = pair.Value.Start, Model = pair.Value.Model,
            FirstTokenMs = pair.Value.FirstTokenMs, Completed = pair.Value.Completed
        });
        return new(now, records, frozenTurns, files.Count, invalidLines,
            cursors.Values.Count(c => c.HasLegacyUsage && !c.HasUsage), failures, watch.Elapsed.TotalMilliseconds);
    }
    public IReadOnlyList<TimedUsage> TimedRecords(double cutoff = double.PositiveInfinity)
    {
        var result = new List<TimedUsage>();
        foreach (var group in usage.Values.Where(r => r.Completed <= cutoff).GroupBy(r => r.Turn))
        {
            turns.TryGetValue(group.Key, out var turn);
            double previous = turn?.Start ?? double.NegativeInfinity;
            foreach (var r in group.OrderBy(r => r.Completed))
            {
                double itemCutoff = previous;
                if (RequestStarts.TryGetValue(r.Thread + "/" + r.Turn, out var boundaries))
                {
                    foreach (double boundary in boundaries)
                        if (boundary >= previous - .25 && boundary < r.Completed)
                            itemCutoff = Math.Max(previous, boundary - .05);
                }
                double? start = null, seconds = null;
                double first = double.PositiveInfinity;
                bool hasDuration = false;
                if (turn != null && r.Output > 0)
                {
                    foreach (var item in turn.Items.Values)
                    {
                        if (item.Start < itemCutoff || item.End > r.Completed + .001) continue;
                        first = Math.Min(first, item.Start);
                        hasDuration |= item.End > item.Start;
                    }
                }
                double elapsed = r.Completed - first;
                if (hasDuration && elapsed > 0) { start = first; seconds = elapsed; }
                result.Add(new(r, turn?.Model ?? "未知模型", start, seconds));
                previous = r.Completed;
            }
        }
        return result;
    }
    public static WindowStats Window(UsageSnapshot snapshot, int seconds, string? model = null)
    {
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        double now = snapshot.GeneratedAt.ToUnixTimeMilliseconds() / 1000d;
        var rows = snapshot.Records.Where(r => r.Usage.Completed > now - seconds && r.Usage.Completed <= now
            && (model == null || r.Model == model)).ToArray();
        var valid = rows.Where(r => r.StreamSeconds is > 0).ToArray();
        long input = rows.Sum(r => r.Usage.Input), cached = rows.Sum(r => r.Usage.Cached);
        var ttft = rows.Select(r => r.Usage.Turn).Distinct().Select(id => snapshot.Turns.GetValueOrDefault(id))
            .Where(t => t?.Completed > now - seconds && t?.Completed <= now && t.FirstTokenMs is > 0)
            .Select(t => t!.FirstTokenMs!.Value).ToArray();
        return new(seconds, valid.Length > 0 ? valid.Sum(r => (double)r.Usage.Output) / valid.Sum(r => r.StreamSeconds!.Value) : null,
            valid.Length, input / (double)seconds, cached / (double)seconds,
            rows.Sum(r => (double)r.Usage.Output) / seconds, rows.Sum(r => (double)r.Usage.Reasoning) / seconds,
            rows.Length * 60d / seconds, rows.Select(r => r.Usage.Thread).Distinct().Count(),
            input > 0 ? cached / (double)input : 0, ttft.Length > 0 ? Median(ttft) : null);
    }
    public static double Median(IEnumerable<double> source)
    {
        var a = source.Order().ToArray();
        if (a.Length == 0) throw new ArgumentException("没有样本");
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
    public static IReadOnlyList<HourStats> Hours(UsageSnapshot snapshot, DateTime date, string? model = null)
    {
        var tz = TimeSpan.FromHours(8);
        var start = new DateTimeOffset(date.Date, tz).ToUnixTimeSeconds();
        var rows = snapshot.Records.Where(r => r.StreamStart >= start && r.StreamStart < start + 86400
            && r.StreamSeconds is > 0 && (model == null || r.Model == model)).ToArray();
        return Enumerable.Range(0, 24).Select(hour =>
        {
            var a = rows.Where(r => DateTimeOffset.FromUnixTimeMilliseconds((long)(r.StreamStart!.Value * 1000))
                .ToOffset(tz).Hour == hour).ToArray();
            return new HourStats($"{hour:00}:00–{hour + 1:00}:00", a.Length,
                a.Length > 0 ? Median(a.Select(r => r.Tps!.Value)) : null,
                a.Length > 0 ? a.Sum(r => (double)r.Usage.Output) / a.Sum(r => r.StreamSeconds!.Value) : null);
        }).ToArray();
    }
}
