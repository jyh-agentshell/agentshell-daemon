using System.Net.Http.Json;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Serialization;
using AgentShell.Protocol.Models;

namespace AgentShell.Daemon.Security;

/// <summary>
/// 向网关一次性登记本机 Ed25519 公钥。
/// 登记令牌只存在于本次调用的内存和 HTTPS 请求体中，绝不写入磁盘或日志。
/// </summary>
public sealed class HostKeyRegistrar
{
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Func<string, byte[]> _loadPublicKey;

    public HostKeyRegistrar(AppConfig config, HttpClient httpClient, Func<string, byte[]>? loadPublicKey = null)
    {
        _config = config;
        _httpClient = httpClient;
        _loadPublicKey = loadPublicKey ?? (path => Ed25519KeyManager.LoadOrCreateKeyPair(path).PublicKey);
    }

    /// <summary>登记当前主机公钥；服务器拒绝时抛出不含令牌的异常。</summary>
    public async Task RegisterAsync(string registrationToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(registrationToken))
            throw new ArgumentException("登记令牌不能为空。", nameof(registrationToken));

        var publicKey = _loadPublicKey(_config.Binding.KeyPath);
        if (publicKey.Length != 32)
            throw new InvalidOperationException("本机 Ed25519 公钥长度无效。");

        var endpoint = new Uri(new Uri(_config.Reporting.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute), "hosts/register-key");
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("主机公钥登记必须使用 HTTPS。");

        using var response = await _httpClient.PostAsJsonAsync(
            endpoint,
            new RegisterHostKeyRequest(registrationToken, _config.Reporting.HostId, Convert.ToBase64String(publicKey)),
            DaemonJsonContext.Default.RegisterHostKeyRequest,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"主机公钥登记被服务器拒绝（HTTP {(int)response.StatusCode}）。");
    }
}
