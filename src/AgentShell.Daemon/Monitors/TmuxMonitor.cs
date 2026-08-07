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

    /// <inheritdoc />
    public async Task<string> CapturePaneAsync(string sessionName, CancellationToken ct = default)
    {
        try
        {
            var result = await RunTmuxAsync(
                $"capture-pane -p -t \"{sessionName}\" -S -200",
                ct);
            IsHealthy = result.ExitCode == 0;
            return result.ExitCode == 0 ? result.Stdout : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "捕获 tmux 会话 {SessionName} 屏幕失败", sessionName);
            IsHealthy = false;
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task SendKeysAsync(string sessionName, string keys, CancellationToken ct = default)
    {
        try
        {
            // 对按键序列进行 tmux 安全转义
            var escaped = keys
                .Replace(";", "\\;")
                .Replace("\n", "Enter");

            var result = await RunTmuxAsync(
                $"send-keys -t \"{sessionName}\" \"{escaped}\"",
                ct);
            IsHealthy = result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "向 tmux 会话 {SessionName} 发送按键失败", sessionName);
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
