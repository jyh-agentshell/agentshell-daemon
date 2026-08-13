using System.Diagnostics;
using System.Text.RegularExpressions;
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
/// 监控与上报合并在单个循环中，消除并发读写竞态。
/// </summary>
public sealed class DaemonService : BackgroundService
{
    private readonly IMonitorTarget _monitor;
    private readonly IApiReporter _reporter;
    private readonly AppConfig _config;
    private readonly ILogger<DaemonService> _logger;
    private readonly string _daemonVersion;

    // 每个会话独立追踪状态，避免多个会话相互覆盖。
    private readonly Dictionary<string, SessionState> _sessionStates = new(StringComparer.Ordinal);

    // 已知会话集合（用于生命周期检测）
    private readonly HashSet<string> _knownSessions = new(StringComparer.Ordinal);

    // CLI 工具进程名映射
    private static readonly Dictionary<string, AgentType> AgentProcessMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = AgentType.Codex,
        ["claude"] = AgentType.Claude,
        ["ocode"] = AgentType.OpenCode,
        ["aider"] = AgentType.Aider
    };

    // 正则模式（回退路径：匹配常见 CLI 审批提示）
    // 支持: Codex "(y/n/d/r)", Claude Code "(y)es/(n)o", 通用 "[y/N]", "Approve? (y/n)"
    private static readonly Regex ApprovalPattern = new(
        @"(?:approve|accept|confirm|proceed)\??\s*(?:changes|edit|modification|diff)?\s*[\[\(]?\s*[yYy]\s*[/,]\s*[nNdDrR]|"
        + @"[\[\(]\s*[yY]\s*[/,]\s*[nN]\s*[\]\)]|"
        + @"\(y\)es\s*/\s*\(n\)o|"
        + @"\(y\)\s*/\s*\(n\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    // 错误检测正则
    private static readonly Regex ErrorPattern = new(
        @"\b(?:error|ERROR|failed|FAILED|exception|traceback)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
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
        _daemonVersion = GetType().Assembly.GetName().Version?.ToString() ?? "0.2.0";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentShell daemon v{Version} 已启动。监控类型: {Type}, 会话模式: {Pattern}",
            _daemonVersion, _monitor.Type, _config.Monitor.SessionPattern);

        // 单一循环：监控 + 状态变化时上报
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "监控轮询异常");
            }

            await Task.Delay(_config.Monitor.PollIntervalMs, stoppingToken);
        }
    }

    /// <summary>
    /// 单次监控轮询：检测状态 → 仅在变化时上报。
    /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        // 未绑定或令牌不可用时不扫描会话；绑定成功后下一轮自动发送完整初始状态。
        if (!await _reporter.IsReadyToReportAsync(ct))
            return;

        var sessions = await _monitor.GetSessionsAsync(ct);

        // 会话生命周期检测
        await DetectSessionLifecycleAsync(sessions, ct);

        // 过滤匹配 glob 的会话
        var matchingSessions = FilterSessions(sessions);
        if (matchingSessions.Count == 0) return;

        foreach (var sessionName in matchingSessions.OrderBy(s => s, StringComparer.Ordinal))
        {
            try
            {
                var sessionId = BuildSessionId(sessionName);
                var screenContent = await _monitor.CapturePaneAsync(sessionName, ct);
                if (string.IsNullOrEmpty(screenContent))
                    continue;

                var (newState, detail, source) = DetectState(screenContent);
                var wasTracked = _sessionStates.TryGetValue(sessionName, out var previous);
                var agentType = previous?.AgentType ?? AgentType.None;

                if (newState != AgentState.Idle && agentType == AgentType.None)
                {
                    agentType = DetectAgentType(screenContent);
                    if (agentType != AgentType.None)
                        _logger.LogInformation("会话 {Session} 检测到 Agent: {AgentType}", sessionName, agentType);
                }

                if (wasTracked && newState == previous!.State)
                    continue;

                var previousState = wasTracked ? previous!.State : AgentState.Idle;
                _logger.LogInformation("状态变更: {SessionId} {PreviousState}→{State}（来源: {Source}）",
                    sessionId, previousState, newState, source);

                var evt = new AgentStateEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTimeOffset.UtcNow,
                    SessionId = sessionId,
                    AgentType = agentType,
                    State = newState,
                    PreviousState = previousState,
                    Detail = detail,
                    Source = source,
                    ProtocolVersion = "0.3.1",
                    DaemonVersion = _daemonVersion
                };

                var result = await _reporter.ReportAgentStateAsync(evt, ct);
                if (result == ReportResult.Accepted)
                {
                    _sessionStates[sessionName] = new SessionState(newState, agentType);
                }
                else
                {
                    _logger.LogWarning("状态上报未被确认: {SessionId} ({Result})，将在下次轮询重试", sessionId, result);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "会话 {Session} 的状态检测或上报失败，将在下次轮询重试", sessionName);
            }
        }
    }

    /// <summary>
    /// 检测会话生命周期变化（新增、销毁）。
    /// </summary>
    private async Task DetectSessionLifecycleAsync(IReadOnlyList<string> currentSessions, CancellationToken ct)
    {
        var currentSet = new HashSet<string>(currentSessions, StringComparer.Ordinal);

        // 新增会话
        foreach (var session in currentSet)
        {
            if (!_knownSessions.Contains(session))
            {
                _logger.LogInformation("会话创建: {Session}", session);

                var lifecycleEvt = new SessionLifecycleEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTimeOffset.UtcNow,
                SessionId = BuildSessionId(session),
                    EventType = SessionEventType.Created,
                    MultiplexerType = MapMultiplexerType(_monitor.Type),
                    SessionName = session,
                    AgentType = AgentType.None,
                    ProtocolVersion = "0.3.1",
                    DaemonVersion = _daemonVersion
                };
                if (await _reporter.ReportSessionLifecycleAsync(lifecycleEvt, ct) == ReportResult.Accepted)
                    _knownSessions.Add(session);
            }
        }

        // 销毁的会话
        var destroyed = _knownSessions.Where(s => !currentSet.Contains(s)).ToList();
        foreach (var session in destroyed)
        {
            _logger.LogInformation("会话销毁: {Session}", session);

            var lifecycleEvt = new SessionLifecycleEvent
            {
                EventId = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                    SessionId = BuildSessionId(session),
                EventType = SessionEventType.Destroyed,
                MultiplexerType = MapMultiplexerType(_monitor.Type),
                SessionName = session,
                AgentType = AgentType.None,
                ProtocolVersion = "0.3.1",
                DaemonVersion = _daemonVersion
            };
            if (await _reporter.ReportSessionLifecycleAsync(lifecycleEvt, ct) == ReportResult.Accepted)
                _knownSessions.Remove(session);
        }

        foreach (var session in _sessionStates.Keys.Where(s => !currentSet.Contains(s)).ToArray())
            _sessionStates.Remove(session);
    }

    /// <summary>
    /// 根据 glob 模式过滤会话列表。
    /// </summary>
    private List<string> FilterSessions(IReadOnlyList<string> sessions)
    {
        var pattern = _config.Monitor.SessionPattern;
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return [..sessions];

        // 简单的 glob: * 匹配任意字符
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        var regex = new Regex(regexPattern, RegexOptions.CultureInvariant);

        return sessions.Where(s => regex.IsMatch(s)).ToList();
    }

    /// <summary>
    /// 双路径状态检测：ANSI OSC 标记优先，正则回退。
    /// </summary>
    private (AgentState NewState, AgentStateDetail? Detail, StateSource Source) DetectState(string content)
    {
        // 路径 A：ANSI OSC 结构化标记（取最后一条匹配，因为终端中最新的在底部）
        var oscResult = TryParseOscMarker(content);
        if (oscResult.HasValue)
        {
            _logger.LogTrace("状态检测: OSC 标记 → {State}", oscResult.Value.state);
            return (oscResult.Value.state, oscResult.Value.fileCount.HasValue
                ? new AgentStateDetail { FileCount = oscResult.Value.fileCount }
                : null, StateSource.OscMarker);
        }

        // 路径 B：正则回退
        if (ApprovalPattern.IsMatch(content))
        {
            _logger.LogTrace("状态检测: 正则回退 → AwaitingApproval");
            return (AgentState.AwaitingApproval, null, StateSource.RegexFallback);
        }

        if (ErrorPattern.IsMatch(content))
        {
            _logger.LogTrace("状态检测: 正则回退 → Error");
            return (AgentState.Error, null, StateSource.RegexFallback);
        }

        // 回退默认：Idle
        return (AgentState.Idle, null, StateSource.RegexFallback);
    }

    /// <summary>
    /// 尝试解析 ANSI OSC 标记，格式: ESC ] 9 ; agent_state=STATE[; KEY=VALUE]* BEL。
    /// 取最后一条匹配行（终端输出中越靠后的行越新）。
    /// </summary>
    private (AgentState state, int? fileCount)? TryParseOscMarker(string content)
    {
        const string prefix = "]9;";
        const string bell = "";

        (AgentState state, int? fileCount)? lastMatch = null;

        foreach (var line in content.Split('\n'))
        {
            var idx = line.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0) continue;

            var endIdx = line.IndexOf(bell, idx + prefix.Length, StringComparison.Ordinal);
            if (endIdx < 0) continue;

            var body = line[(idx + prefix.Length)..endIdx].Trim();
            var parts = body.Split(';', StringSplitOptions.RemoveEmptyEntries);

            AgentState? state = null;
            int? fileCount = null;

            foreach (var part in parts)
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex <= 0) continue;

                var key = part[..separatorIndex].Trim();
                if (key is not "agent_state" and not "files") continue;

                var value = part[(separatorIndex + 1)..].Trim();

                switch (key)
                {
                    case "agent_state":
                        state = value switch
                        {
                            "running" => AgentState.Running,
                            "awaiting_approval" => AgentState.AwaitingApproval,
                            "idle" => AgentState.Idle,
                            "error" => AgentState.Error,
                            "terminated" => AgentState.Terminated,
                            _ => AgentState.Running
                        };
                        break;
                    case "files":
                        if (int.TryParse(value, out var fc)) fileCount = fc;
                        break;
                }
            }

            if (state.HasValue)
                lastMatch = (state.Value, fileCount);
        }

        return lastMatch;
    }

    /// <summary>
    /// 检测终端内容中正在运行的 Agent CLI 工具类型。
    /// 优先通过 OSC 标记，其次通过已知提示符特征。
    /// </summary>
    private AgentType DetectAgentType(string content)
    {
        // 优先：OSC 标记中的 agent_type 字段（更可靠）
        foreach (var line in content.Split('\n'))
        {
            if (!line.Contains("agent_type=")) continue;

            var idx = line.IndexOf("agent_type=", StringComparison.Ordinal);
            if (idx < 0) continue;

            var valStart = idx + "agent_type=".Length;
            var valEnd = line.IndexOfAny([';', '\a'], valStart);
            if (valEnd < 0) valEnd = line.Length;
            var val = line[valStart..valEnd].Trim();
            if (AgentProcessMap.TryGetValue(val, out var detected))
                return detected;
        }

        // 回退：检查进程名特征（仅在包含 CLI 提示符上下文的行中检查，减少误报）
        // 只在包含典型 CLI prompt 特征的行中查找
        var promptLines = content.Split('\n')
            .Where(l => l.Contains('>') || l.Contains('$') || l.Contains("❯") || l.Contains('#'))
            .ToArray();

        if (promptLines.Length > 0)
        {
            var promptText = string.Join("\n", promptLines).ToLowerInvariant();
            foreach (var (keyword, agentType) in AgentProcessMap)
            {
                if (promptText.Contains(keyword))
                    return agentType;
            }
        }

        return AgentType.Unknown;
    }

    private static MultiplexerType MapMultiplexerType(string type) => type switch
    {
        "tmux" => MultiplexerType.Tmux,
        "screen" => MultiplexerType.Screen,
        "zellij" => MultiplexerType.Zellij,
        "pty" => MultiplexerType.Pty,
        _ => MultiplexerType.Tmux
    };

    private string BuildSessionId(string sessionName) =>
        $"{_config.Reporting.HostId}/{_monitor.Type}/{sessionName}";

    private sealed record SessionState(AgentState State, AgentType AgentType);
}
