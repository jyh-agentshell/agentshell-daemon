using System.Diagnostics;
using AgentShell.Daemon.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentShell.Daemon.Monitors;

/// <summary>
/// tmux 终端复用器的监控实现。
/// 通过调用 tmux CLI 命令来捕获屏幕内容、发送按键和管理会话。
/// </summary>
public sealed class TmuxMonitor : IMonitorTarget
{
    private readonly ILogger<TmuxMonitor> _logger;
    private readonly AppConfig _config;

    public string Type => "tmux";
    public bool IsHealthy { get; private set; } = true;

    public TmuxMonitor(ILogger<TmuxMonitor> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunTmuxAsync("list-sessions -F '#{session_name}'", ct);
            if (result.ExitCode != 0) return [];

            return result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 tmux 会话列表失败");
            IsHealthy = false;
            return [];
        }
    }

    // tmux 会话名允许的字符集：字母、数字、点、下划线、短横线
    private static readonly System.Text.RegularExpressions.Regex ValidSessionPattern =
        new(@"^[a-zA-Z0-9._\-]+$",
            System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// 验证并净化会话名，防止命令注入。
    /// 如果会话名包含非法字符，返回空字符串并记日志。
    /// </summary>
    private string SanitizeSessionName(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            _logger.LogWarning("会话名为空，拒绝操作");
            return string.Empty;
        }

        var trimmed = sessionName.Trim();
        if (!ValidSessionPattern.IsMatch(trimmed))
        {
            _logger.LogWarning("会话名包含非法字符，拒绝操作: {SessionName}", trimmed);
            return string.Empty;
        }

        return trimmed;
    }

    /// <inheritdoc />
    public async Task<string> CapturePaneAsync(string sessionName, CancellationToken ct = default)
    {
        try
        {
            var safe = SanitizeSessionName(sessionName);
            if (string.IsNullOrEmpty(safe))
                return string.Empty;

            var result = await RunTmuxAsync(
                $"capture-pane -p -t \"{safe}\" -S -200",
                ct);
            IsHealthy = result.ExitCode == 0;
            return result.ExitCode == 0 ? result.Stdout : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "捕获 tmux 会话屏幕失败");
            IsHealthy = false;
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task SendKeysAsync(string sessionName, string keys, CancellationToken ct = default)
    {
        try
        {
            var safe = SanitizeSessionName(sessionName);
            if (string.IsNullOrEmpty(safe))
                return;

            // 对按键序列进行 tmux 安全转义
            var escaped = keys
                .Replace(";", "\\;")
                .Replace("\n", "Enter");

            var result = await RunTmuxAsync(
                $"send-keys -t \"{safe}\" \"{escaped}\"",
                ct);
            IsHealthy = result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "向 tmux 会话发送按键失败");
            IsHealthy = false;
        }
    }

    public void Dispose() { }

    /// <summary>
    /// 执行 tmux 命令并返回 stdout/stderr。
    /// </summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunTmuxAsync(
        string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tmux",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }
}
