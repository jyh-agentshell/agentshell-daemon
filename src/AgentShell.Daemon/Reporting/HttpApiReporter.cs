using System.Net.Http.Json;
using System.Text.Json;
using AgentShell.Daemon.Auth;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Security;
using AgentShell.Daemon.Serialization;
using AgentShell.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace AgentShell.Daemon.Reporting;

/// <summary>
/// 真实 HTTPS 上报客户端。
/// 通过 TokenManager 获取认证 Token，向 .NET 网关上报 Agent 状态和会话生命周期事件。
/// </summary>
public sealed class HttpApiReporter : IApiReporter, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private readonly ILogger<HttpApiReporter> _logger;
    private readonly string _daemonVersion;
    private readonly ReportSigner _reportSigner;
    private readonly string _hostId;

    public HttpApiReporter(
        AppConfig config,
        TokenManager tokenManager,
        ILogger<HttpApiReporter> logger,
        TimeProvider? clock = null)
    {
        if (!IsSecureApiBaseUrl(config.Reporting.ApiBaseUrl))
            throw new InvalidOperationException("reporting.api_base_url 必须使用 HTTPS。");

        _tokenManager = tokenManager;
        _logger = logger;
        _daemonVersion = GetType().Assembly.GetName().Version?.ToString() ?? "0.3.1";
        _hostId = config.Reporting.HostId;
        var (privateKey, _) = Ed25519KeyManager.LoadOrCreateKeyPair(config.Binding.KeyPath);
        _reportSigner = new ReportSigner(privateKey, clock ?? TimeProvider.System);

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Reporting.ApiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Agentshell-Daemon-Version", _daemonVersion);
    }

    public async Task<bool> IsReadyToReportAsync(CancellationToken ct = default) =>
        await _tokenManager.GetAccessTokenAsync(ct) != null;

    public async Task<ReportResult> ReportAgentStateAsync(AgentStateEvent stateEvent, CancellationToken ct = default)
    {
        var token = await _tokenManager.GetAccessTokenAsync(ct);
        if (token == null)
        {
            _logger.LogWarning("无法获取 Access Token（状态: {State}），跳过上报", _tokenManager.CurrentState);
            return ReportResult.AuthenticationRequired;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/report")
        {
            Content = JsonContent.Create(
                CreateEnvelope("agent_state", stateEvent, DaemonJsonContext.Default.AgentStateEvent),
                DaemonJsonContext.Default.ReportEnvelope)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try { response = await _httpClient.SendAsync(request, ct); }
        catch (HttpRequestException) { return ReportResult.RetryableFailure; }
        using (response)
        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("状态上报成功: {SessionId} → {State}", stateEvent.SessionId, stateEvent.State);
        }
        else
        {
            _logger.LogWarning("状态上报失败: {StatusCode} ({SessionId})", response.StatusCode, stateEvent.SessionId);
        }
        return ToReportResult(response.StatusCode);
    }

    public async Task<ReportResult> ReportSessionLifecycleAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken ct = default)
    {
        var token = await _tokenManager.GetAccessTokenAsync(ct);
        if (token == null)
        {
            _logger.LogWarning("无法获取 Access Token（状态: {State}），跳过上报", _tokenManager.CurrentState);
            return ReportResult.AuthenticationRequired;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/lifecycle")
        {
            Content = JsonContent.Create(
                CreateEnvelope("session_lifecycle", lifecycleEvent, DaemonJsonContext.Default.SessionLifecycleEvent),
                DaemonJsonContext.Default.ReportEnvelope)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try { response = await _httpClient.SendAsync(request, ct); }
        catch (HttpRequestException) { return ReportResult.RetryableFailure; }
        using (response)
        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("生命周期上报成功: {SessionId}", lifecycleEvent.SessionId);
        }
        else
        {
            _logger.LogWarning("生命周期上报失败: {StatusCode} ({SessionId})", response.StatusCode, lifecycleEvent.SessionId);
        }
        return ToReportResult(response.StatusCode);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var token = await _tokenManager.GetAccessTokenAsync(ct);
        if (token == null)
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "health");
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public static bool IsSecureApiBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private ReportEnvelope CreateEnvelope<T>(
        string payloadType,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo) =>
        _reportSigner.Sign(
            _hostId,
            payloadType,
            JsonSerializer.SerializeToUtf8Bytes(payload, jsonTypeInfo),
            ["agent_state", "session_lifecycle"]);

    private static ReportResult ToReportResult(System.Net.HttpStatusCode statusCode) =>
        ((int)statusCode) switch
        {
            >= 200 and < 300 => ReportResult.Accepted,
            401 => ReportResult.AuthenticationRequired,
            426 => ReportResult.IncompatibleProtocol,
            409 or 429 or >= 500 => ReportResult.RetryableFailure,
            _ => ReportResult.Rejected
        };
}
