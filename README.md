# Codex TPS 中文悬浮窗

Windows 10/11 x64 的本机日志监视工具。白色圆角面板参考 [codex-tps-panel.png](https://github.com/gaofeng21cn/codex-tps/blob/main/docs/assets/codex-tps-panel.png)，代码为独立实现。

## 使用

双击 `app/CodexTpsFloat.exe`。便携版包含运行时，不需要安装 Python 或 .NET，不需要管理员权限。

GitHub 源码仓库不包含可执行文件和本机运行数据。克隆源码后，使用 .NET 10 SDK 运行 `./build.ps1 -Portable` 生成 `app/CodexTpsFloat.exe`。本机交付目录已包含构建好的程序。隐私与提交范围见 [PRIVACY.md](PRIVACY.md)。

- 默认读取 `CODEX_HOME`；未设置时读取当前用户的 `.codex`。同时统计 `sessions` 与 `archived_sessions`。
- 拖动标题区域移动窗口，蓝色菱形切换置顶，减号收起到系统托盘；双击托盘图标恢复。电源按钮退出。
- 切换 1 分钟、5 分钟、30 分钟、1 小时窗口，或筛选模型。自动刷新支持 5、15、30、60 秒。
- 展开「今日分时」，可以选择其他日期并导出中文 Markdown 或 JSON 报告。时区固定为北京时间 UTC+8。
- 「设置」可以选择数据目录。选择包含 `sessions` 的 `.codex` 目录，而不是 `sessions` 自身。
- 「登录启动」默认关闭，仅在手动勾选后设置当前用户的启动项。取消勾选即可移除。
- 第二次启动会显示已有悬浮窗，不重复运行监视进程。窗口位置、刷新周期、置顶和时间窗口保存在程序旁的 `data/settings.json`。

## 数字的含义

**大号读数是输出生成速度估算**：所选窗口内已完成响应的输出词元总数，除以各响应流式阶段耗时之和。包含推理词元；并发响应耗时分别相加，输入词元不计入。

流式阶段使用日志中的首个 `AgentMessage` / `Reasoning` 输出项开始时间至逐次 `token_usage_record` 时间。日志不是逐 token 计时；该数字不能当作服务端精确解码速度。缺少计时或只有零持续时间的输出项显示为无样本，不补成零。

**四色小读数是窗口吞吐量**：窗口内完成的对应词元数量 ÷ 完整窗口秒数。缓存是输入的子集，推理是输出的子集，均不重复相加。这个口径与参考项目的大号「总 token 吞吐量」有区别。

**轮次首字**是已经结束轮次的 `time_to_first_token_ms` 中位数，不代表每个内部请求的 TTFT。活跃会话以当前窗口内有已完成用量事件为准。

分时统计按首个输出项的北京时间小时分组。不同日期、时段的任务和上下文不同，不能单凭速度变化断定服务端或网络性能变化。现代日志支持逐次 `token_usage_record`；只有旧版 `token_count` 的日志不会虚构逐次输出速度，状态会提示部分日志不可计量。

## 数据与运行方式

工具只读日志，不修改 Codex 配置、日志或请求，不需要 API Key，不调用网络服务。读取在后台线程完成，后续刷新只读追加内容；完整 JSONL 尾行才计入。按 `response_id` 跨文件去重；归档、重复刷新不会重复计数。日志截断或替换后重建统计。

如果当前数据目录存在 `logs_*.sqlite`，以只读方式增量读取请求鉴权边界，用于排除失败尝试遗留的输出项。诊断库缺失、被锁定或格式不兼容时仍可使用 JSONL 输出项计时，状态提示中说明降级。边界日志可能已过期，因此仍是估算值。

输出不保存提示词或回答正文。配置写入程序旁目录，导出报告的位置由保存对话框选择。若程序目录只读，统计仍可运行，但设置无法保存。

可用于脚本的单次快照：

```powershell
.\app\CodexTpsFloat.exe --snapshot-json .\snapshot.json
.\app\CodexTpsFloat.exe --codex-home 'D:\CodexData' --snapshot-json .\snapshot.json
```

退出码：`0` 成功；`1` 读取或写入失败；`2` 参数错误；`3` 未找到会话日志。快照模式独立执行，JSON 包含四个滚动窗口和今日分时汇总，不包含正文。

从源码构建需要 .NET 10 SDK：

```powershell
dotnet run --project .\tests\CoreTests.csproj -c Release
.\build.ps1 -Portable
```

源码中的 `--capture 文件.png` 用于界面验收，生成真实 WPF 控件渲染图后退出。便携 EXE 为本地构建，未进行 Authenticode 签名。
