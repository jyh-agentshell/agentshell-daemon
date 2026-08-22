using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Serialization;
using AgentShell.Daemon.Security;
using AgentShell.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace AgentShell.Daemon.Auth;

/// <summary>
/// Token 生命周期管理器。
/// 管理 daemon Access Token 的加载、有效期监控、自动续期和状态转换。
///
/// 状态机: Active → Renewing → Active（成功）
///                   → Retry（临时失败，退避重试）
///                   → AwaitingBinding（403 或重试耗尽）
/// </summary>
public sealed class TokenManager : IDisposable
{
    public enum State { AwaitingBinding, Active, Renewing, Retry }

    private readonly AppConfig _config;
    private readonly TokenStore _store;
    private readonly ILogger<TokenManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly JwtSecurityTokenHandler _jwtHandler;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private readonly bool _ownsHttpClient;

    private string? _currentToken;
    private State _state = State.AwaitingBinding;
    private int _retryCount;
    private DateTime _tokenExpiry = DateTime.MinValue;

    // 续期阈值：Token 剩余有效期 < RenewalThreshold 时触发续期
    private static readonly TimeSpan RenewalThreshold = TimeSpan.FromMinutes(10);
    // 最大重试次数
    private const int MaxRetries = 5;
    // 退避间隔
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16)
    ];

    public TokenManager(
        AppConfig config,
        TokenStore store,
        ILogger<TokenManager> logger,
        TimeProvider? clock = null,
        HttpClient? httpClient = null)
    {
        _config = config;
        _store = store;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(config.Reporting.ApiBaseUrl.TrimEnd('/') + "/")
        };
        _jwtHandler = new JwtSecurityTokenHandler();
    }

    public State CurrentState => _state;
    public string? CurrentToken => _currentToken;

    /// <summary>
    /// 初始化：从本地文件加载 Token。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var saved = await _store.LoadAsync(ct);
        if (saved != null)
        {
            _currentToken = saved;
            _tokenExpiry = ReadExpiry(saved);
            if (_tokenExpiry > _clock.GetUtcNow().UtcDateTime)
            {
                _state = State.Active;
                _logger.LogInformation("已加载本地 Token，过期时间: {Expiry:O}", _tokenExpiry);
            }
            else
            {
                _logger.LogInformation("本地 Token 已过期，尝试续期");
                _state = State.Renewing;
            }
        }
        else
        {
            _logger.LogInformation("TokenManager 初始化：无本地 Token，等待绑定");
            _state = State.AwaitingBinding;
        }
    }

    /// <summary>
    /// 获取有效 Access Token。必要时触发自动续期。
    /// 调用方（HttpApiReporter）每次上报前调用此方法。
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // DaemonService 可能同时触发多个上报；续期请求带有一次性 anti-replay
        // 标识，必须串行化，否则同一旧 Token 的并发续期会互相返回 409。
        await _renewGate.WaitAsync(ct);
        try
        {
            switch (_state)
            {
                case State.AwaitingBinding:
                    return null;

                case State.Active:
                    // 检查是否需要续期
                    if (_clock.GetUtcNow().UtcDateTime + RenewalThreshold < _tokenExpiry)
                        return _currentToken;
                    _state = State.Renewing;
                    return await RenewTokenAsync(ct);

                case State.Renewing:
                    return await RenewTokenAsync(ct);

                case State.Retry:
                    return await RetryRenewAsync(ct);

                default:
                    return null;
            }
        }
        finally
        {
            _renewGate.Release();
        }
    }

    /// <summary>
    /// 设置 Token（绑定完成后由外部调用，如通过 CLI 或 API 收到首个 Token）。
    /// </summary>
    public async Task SetTokenAsync(string token, CancellationToken ct = default)
    {
        _currentToken = token;
        _tokenExpiry = ReadExpiry(token);
        _retryCount = 0;
        _state = State.Active;
        await _store.SaveAsync(token, ct);
        _logger.LogInformation("Token 已设置，过期时间: {Expiry:O}", _tokenExpiry);
    }

    private async Task<string?> RenewTokenAsync(CancellationToken ct)
    {
        if (_currentToken == null)
        {
            _state = State.AwaitingBinding;
            return null;
        }

        try
        {
            var jti = ReadJti(_currentToken);
            if (jti == null)
            {
                _state = State.AwaitingBinding;
                return null;
            }

            var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds();
            var message = System.Text.Encoding.UTF8.GetBytes($"renew:{jti}:{timestamp}");

            // 加载 Ed25519 密钥对
            var (privateKey, _) = Ed25519KeyManager.LoadOrCreateKeyPair(_config.Binding.KeyPath);
            var signature = Ed25519KeyManager.Sign(privateKey, message);

            // 发起续期请求
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/renew");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _currentToken);
            request.Headers.Add("X-Agentshell-Signature", Convert.ToBase64String(signature));
            request.Headers.Add("X-Agentshell-Timestamp", timestamp.ToString());

            var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // 403: 主机已撤销
                _logger.LogWarning("续期返回 403：主机公钥可能已被撤销");
                _state = State.AwaitingBinding;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("续期请求失败: {StatusCode}", response.StatusCode);
                _state = State.Retry;
                return _retryCount < MaxRetries ? _currentToken : null;
            }

            var renewResponse = await response.Content.ReadFromJsonAsync(DaemonJsonContext.Default.RenewResponse, ct);

            if (renewResponse == null || string.IsNullOrEmpty(renewResponse.AccessToken))
            {
                _logger.LogWarning("续期响应无效");
                _state = State.Retry;
                return _retryCount < MaxRetries ? _currentToken : null;
            }

            // 续期成功
            await SetTokenAsync(renewResponse.AccessToken, ct);
            _retryCount = 0;
            _logger.LogInformation("Token 续期成功，新过期时间: {Expiry:O}", _tokenExpiry);
            return _currentToken;

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "续期异常");
            _state = State.Retry;
            return _retryCount < MaxRetries ? _currentToken : null;
        }
    }

    private async Task<string?> RetryRenewAsync(CancellationToken ct)
    {
        if (_retryCount >= MaxRetries)
        {
            _logger.LogError("续期重试耗尽（{MaxRetries} 次），进入 AwaitingBinding 状态", MaxRetries);
            _state = State.AwaitingBinding;
            return null;
        }

        var delay = RetryDelays[_retryCount];
        _retryCount++;
        _logger.LogInformation("续期退避重试 {Retry}/{Max}, 等待 {Delay}ms",
            _retryCount, MaxRetries, delay.TotalMilliseconds);

        await Task.Delay(delay, ct);
        _state = State.Renewing;
        return await RenewTokenAsync(ct);
    }

    private DateTime ReadExpiry(string token)
    {
        try
        {
            var jwt = _jwtHandler.ReadJwtToken(token);
            return jwt.ValidTo;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private string? ReadJti(string token)
    {
        try
        {
            var jwt = _jwtHandler.ReadJwtToken(token);
            return jwt.Id;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
        _renewGate.Dispose();
    }
}
