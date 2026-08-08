using System.Diagnostics;
using System.Text;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Monitors;
using AgentShell.Daemon.Reporting;
using AgentShell.Protocol.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentShell.Daemon.Services;

/// <summary>
/// 守护进程核心服务。
/// 实现 IHostedService，在后台持续轮询 Agent 状态并上报。
/// </summary>
public sealed class DaemonService : BackgroundService
{
    private readonly IMonitorTarget _monitor;
    private readonly IApiReporter _reporter;
    private readonly AppConfig _config;
    private readonly ILogger<DaemonService> _logger;
    private readonly string _daemonVersion;

    // 状态检测相关
    private string? _currentSessionId;
    private AgentState _currentAgentState = AgentState.Idle;
    private AgentType _currentAgentType = AgentType.None;
    private StateSource _currentSource = StateSource.OscMarker;

    // CLI 工具进程名/提示符映射
    private static readonly Dictionary<string, AgentType> AgentProcessMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = AgentType.Codex,
        ["claude"] = AgentType.Claude,
        ["ocode"] = AgentType.OpenCode,
        ["aider"] = AgentType.Aider
    };

    // 正则模式（回退路径：匹配常见 CLI 审批提示）
    private static readonly System.Text.RegularExpressions.Regex ApprovalPattern = new(
        @"(approve|accept|confirm|proceed)\??\s*(changes|edit|modification|diff)?\s*[\[\(]?\s*[yYnN]\s*[/,]\s*[nNdDrR]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public DaemonService(
        IMonitorTarget monitor,
        IApiReporter reporter,
        AppConfig config,
        ILogger<DaemonService> logger)
    {
        _monitor = monitor;
        _reporter = reporter;
        _config = config;
        _logger = logger;
        _daemonVersion = GetType().Assembly.GetName().Version?.ToString() ?? "0.1.0";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentShell daemon v{Version} 已启动。监控类型: {Type}",
            _daemonVersion, _monitor.Type);

        await Task.WhenAll(
            MonitorLoopAsync(stoppingToken),
            ReportLoopAsync(stoppingToken)
        );
    }

    /// <summary>
    /// 监控循环：轮询 tmux 会话状态
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception ex)
            {
                // 任何异常静默处理，守护进程不崩
                _logger.LogWarning(ex, "监控轮询异常");
            }

            await Task.Delay(_config.Monitor.PollIntervalMs, ct);
        }
    }

    /// <summary>
    /// 单次监控轮询
    /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        var sessions = await _monitor.GetSessionsAsync(ct);
        if (sessions.Count == 0) return;

        // 取第一个匹配的会话
        var sessionName = sessions[0];
        var sessionId = $"{Environment.MachineName}/{_monitor.Type}/{sessionName}";

        // 捕获屏幕内容
        var screenContent = await _monitor.CapturePaneAsync(sessionName, ct);
        if (string.IsNullOrEmpty(screenContent)) return;

        // 双路径检测
        var (newState, detail, source) = DetectState(screenContent);

        // 状态变更时更新追踪
        if (sessionId != _currentSessionId || newState != _currentAgentState)
        {
            _logger.LogInformation("状态变更: {SessionId} → {State}（来源: {Source}）",
                sessionId, newState, source);

            _currentSessionId = sessionId;
            _currentAgentState = newState;
            _currentSource = source;

            // 自动探测 Agent 类型
            if (newState != AgentState.Idle && _currentAgentType == AgentType.None)
            {
                _currentAgentType = DetectAgentType(screenContent);
                if (_currentAgentType != AgentType.None)
                    _logger.LogInformation("检测到 Agent: {AgentType}", _currentAgentType);
            }
        }
    }

    /// <summary>
    /// 双路径状态检测：ANSI OSC 标记优先，正则回退。
    /// </summary>
    private (AgentState NewState, AgentStateDetail? Detail, StateSource Source) DetectState(string content)
    {
        // 路径 A：ANSI OSC 结构化标记
        var oscResult = TryParseOscMarker(content);
        if (oscResult.HasValue)
        {
            _logger.LogTrace("状态检测: OSC 标记 → {State}", oscResult.Value.state);
            return (oscResult.Value.state, new AgentStateDetail
            {
                Message = oscResult.Value.message,
                Prompt = oscResult.Value.prompt,
                FileCount = oscResult.Value.fileCount
            }, StateSource.OscMarker);
        }

        // 路径 B：正则回退
        var match = ApprovalPattern.Match(content);
        if (match.Success)
        {
            _logger.LogTrace("状态检测: 正则回退 → AwaitingApproval");
            return (AgentState.AwaitingApproval, new AgentStateDetail
            {
                Message = "检测到审批提示"
            }, StateSource.RegexFallback);
        }

        return (AgentState.Idle, null, StateSource.OscMarker);
    }

    /// <summary>
    /// 尝试解析 ANSI OSC 标记: ESC ] 9 ; agent_state=<state>[; <key>=<value>]* BEL
    /// </summary>
    private (AgentState state, string? message, string? prompt, int? fileCount)? TryParseOscMarker(string content)
    {
        const string prefix = "]9;";
        const string bell = "";

        foreach (var line in content.Split('\n'))
        {
            var idx = line.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0) continue;

            var endIdx = line.IndexOf(bell, idx + prefix.Length);
            if (endIdx < 0) continue;

            var body = line[(idx + prefix.Length)..endIdx].Trim();
            var parts = body.Split(';', StringSplitOptions.RemoveEmptyEntries);

            AgentState? state = null;
            string? message = null;
            string? prompt = null;
            int? fileCount = null;

            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;

                var key = kv[0].Trim();
                var value = kv[1].Trim();

                switch (key)
                {
                    case "agent_state":
                        state = value switch
                        {
                            "running" => AgentState.Running,
                            "awaiting_approval" => AgentState.AwaitingApproval,
                            "idle" => AgentState.Idle,
                            "error" => AgentState.Error,
                            _ => AgentState.Running
                        };
                        break;
                    case "message":
                        message = DecodeBase64(value);
                        break;
                    case "prompt":
                        prompt = DecodeBase64(value);
                        break;
                    case "files":
                        if (int.TryParse(value, out var fc)) fileCount = fc;
                        break;
                }
            }

            if (state.HasValue)
                return (state.Value, message, prompt, fileCount);
        }

        return null;
    }

    /// <summary>
    /// 上报循环：定期向网关发送状态
    /// </summary>
    private async Task ReportLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 有活跃会话即上报，不要求 Agent 类型已知
                if (_currentSessionId != null)
                {
                    var evt = new AgentStateEvent
                    {
                        EventId = Guid.NewGuid().ToString(),
                        Timestamp = DateTimeOffset.UtcNow,
                        SessionId = _currentSessionId,
                        AgentType = _currentAgentType,
                        State = _currentAgentState,
                        Source = _currentSource,
                        DaemonVersion = _daemonVersion
                    };

                    await _reporter.ReportAgentStateAsync(evt, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "上报状态失败");
            }

            await Task.Delay(_config.Reporting.ReportIntervalMs, ct);
        }
    }

    /// <summary>
    /// 检测终端内容中正在运行的 Agent CLI 工具类型。
    /// 通过查找已知 CLI 的进程名或提示符特征来推断。
    /// </summary>
    private AgentType DetectAgentType(string content)
    {
        // 检查终端内容中的已知 CLI 提示符特征
        var lower = content.ToLowerInvariant();
        foreach (var (keyword, agentType) in AgentProcessMap)
        {
            if (lower.Contains(keyword))
                return agentType;
        }

        // 通过 OSC 标记中的 agent 类型字段检测
        foreach (var line in content.Split('\n'))
        {
            if (line.Contains("agent_type="))
            {
                var idx = line.IndexOf("agent_type=", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var valStart = idx + "agent_type=".Length;
                    var valEnd = line.IndexOf(';', valStart);
                    if (valEnd < 0) valEnd = line.IndexOf('\a', valStart);
                    if (valEnd < 0) valEnd = line.Length;
                    var val = line[valStart..valEnd].Trim();
                    if (AgentProcessMap.TryGetValue(val, out var detected))
                        return detected;
                }
            }
        }

        return AgentType.Unknown;
    }

    private static string? DecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return value;
        }
    }
}
