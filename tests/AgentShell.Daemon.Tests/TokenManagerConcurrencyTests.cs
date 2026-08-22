using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentShell.Daemon.Auth;
using AgentShell.Daemon.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class TokenManagerConcurrencyTests
{
    [Fact]
    public async Task 并发获取临期Token只发送一次续期请求()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentshell-token-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var tokenPath = Path.Combine(root, "access_token");
        var keyPath = Path.Combine(root, "agent.key");
        var handler = new CountingRenewHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        using var manager = new TokenManager(
            new AppConfig
            {
                Binding = new BindingConfig { KeyPath = keyPath },
                Reporting = new ReportingConfig { ApiBaseUrl = "https://example.test/api" }
            },
            new TokenStore(tokenPath),
            NullLogger<TokenManager>.Instance,
            httpClient: httpClient);

        try
        {
            await manager.SetTokenAsync(CreateToken(DateTimeOffset.UtcNow.AddSeconds(1), "old-jti"));

            var results = await Task.WhenAll(
                manager.GetAccessTokenAsync(),
                manager.GetAccessTokenAsync());

            Assert.Equal(1, handler.RequestCount);
            Assert.All(results, token => Assert.Equal(handler.NewToken, token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateToken(DateTimeOffset expiresAt, string jti)
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode("{\"alg\":\"none\"}");
        var payload = JsonSerializer.Serialize(new { jti, exp = expiresAt.ToUnixTimeSeconds() });
        return $"{header}.{Encode(payload)}.signature";
    }

    private sealed class CountingRenewHandler : HttpMessageHandler
    {
        public int RequestCount;
        public string NewToken { get; } = CreateToken(DateTimeOffset.UtcNow.AddHours(1), "new-jti");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            await Task.Delay(100, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = NewToken, token_type = "Bearer", expires_in = 3600 })
            };
        }
    }
}
