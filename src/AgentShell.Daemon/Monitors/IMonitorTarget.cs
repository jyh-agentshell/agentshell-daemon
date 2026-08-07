namespace AgentShell.Daemon.Monitors;

/// <summary>
/// 终端复用器监控抽象接口。
/// 每种复用器（tmux、screen、zellij）和裸 PTY 均需实现此接口。
/// </summary>
public interface IMonitorTarget : IDisposable
{
    /// <summary>获取当前活跃的会话名称列表</summary>
    Task<IReadOnlyList<string>> GetSessionsAsync(CancellationToken ct = default);

    /// <summary>捕获指定会话的最新屏幕内容</summary>
    /// <param name="sessionName">tmux/screen/zellij 会话名</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>终端文本内容（纯文本）</returns>
    Task<string> CapturePaneAsync(string sessionName, CancellationToken ct = default);

    /// <summary>向指定会话的终端发送按键</summary>
    /// <param name="sessionName">目标会话名</param>
    /// <param name="keys">要发送的按键序列（如 "y\n"）</param>
    /// <param name="ct">取消令牌</param>
    Task SendKeysAsync(string sessionName, string keys, CancellationToken ct = default);

    /// <summary>复用器类型标识</summary>
    string Type { get; }

    /// <summary>最近一次捕获是否成功</summary>
    bool IsHealthy { get; }
}
