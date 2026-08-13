using AgentShell.Protocol.Models;

namespace AgentShell.Daemon.Reporting;

/// <summary>
/// Agent 状态上报客户端接口。
/// 实现类负责通过 HTTPS 将状态事件发送到 .NET 网关。
/// </summary>
public interface IApiReporter
{
    /// <summary>当前是否具备上报所需的有效认证凭据。</summary>
    Task<bool> IsReadyToReportAsync(CancellationToken ct = default) => Task.FromResult(true);

    /// <summary>上报 Agent 状态变化</summary>
    Task<ReportResult> ReportAgentStateAsync(AgentStateEvent stateEvent, CancellationToken ct = default);

    /// <summary>上报会话生命周期事件</summary>
    Task<ReportResult> ReportSessionLifecycleAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken ct = default);

    /// <summary>检查网关连接是否正常</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}

/// <summary>网关对上报的结构化处置结果。</summary>
public enum ReportResult
{
    Accepted,
    RetryableFailure,
    AuthenticationRequired,
    IncompatibleProtocol,
    Rejected
}

/// <summary>
/// 空操作上报器。在 API 网关尚未部署时使用，所有上报操作静默成功。
/// Phase 2 替换为真实 HTTP 客户端实现。
/// </summary>
public sealed class NoOpReporter : IApiReporter
{
    public Task<ReportResult> ReportAgentStateAsync(AgentStateEvent stateEvent, CancellationToken ct = default)
        => Task.FromResult(ReportResult.Accepted);

    public Task<ReportResult> ReportSessionLifecycleAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken ct = default)
        => Task.FromResult(ReportResult.Accepted);

    public Task<bool> PingAsync(CancellationToken ct = default)
        => Task.FromResult(true);
}
