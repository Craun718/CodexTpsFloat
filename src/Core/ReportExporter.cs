using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CodexTpsFloat;

/// <summary>Renders the hourly TPS report that the panel offers as Markdown or JSON.</summary>
public static class ReportExporter
{
    public static string BuildMarkdown(UsageSnapshot snapshot, DateTime date, string? model, IReadOnlyList<HourStats> hours)
    {
        var text = new StringBuilder($"# Codex TPS 分时统计\n\n日期：{date:yyyy-MM-dd}；北京时间 UTC+8；模型：{model ?? "全部模型"}。\n\n快照：{snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}。\n\n| 时段 | 样本 | 中位数 | 加权 TPS |\n|---|---:|---:|---:|\n");
        foreach (var h in hours)
            text.AppendLine($"| {h.Hour} | {h.Samples} | {h.Median?.ToString("F2", CultureInfo.InvariantCulture) ?? "—"} | {h.Weighted?.ToString("F2", CultureInfo.InvariantCulture) ?? "—"} |");
        text.AppendLine("\nTPS 是流式阶段估算：输出词元（包含推理）÷（逐次用量记录时间 − 首个模型输出项开始时间）。输入和缓存输入不计入，不等于精确服务端解码速度。\n\n按首个输出项的北京时间小时分组；零持续时间或缺失计时不作为有效样本。并发响应耗时各自相加。不同任务和上下文不可直接用于认定时段性能差异。\n");
        return text.ToString();
    }
    public static string BuildJson(UsageSnapshot snapshot, DateTime date, string? model, IReadOnlyList<HourStats> hours) =>
        JsonSerializer.Serialize(new
        {
            date = date.ToString("yyyy-MM-dd"),
            timezone = "UTC+8",
            snapshot.GeneratedAt,
            model = model ?? "全部模型",
            method = "输出词元（含推理）除以输出项计时区间；非精确解码速度",
            hours
        }, new JsonSerializerOptions { WriteIndented = true });
    /// <summary>Writes a report; the file extension selects Markdown or JSON.</summary>
    public static void Write(string path, UsageSnapshot snapshot, DateTime date, string? model, IReadOnlyList<HourStats> hours)
    {
        bool json = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
        string content = json ? BuildJson(snapshot, date, model, hours) : BuildMarkdown(snapshot, date, model, hours);
        File.WriteAllText(path, content, Encoding.UTF8);
    }
}
