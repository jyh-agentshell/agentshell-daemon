using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Security;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class HostKeyRegistrarTests
{
    [Fact]
    public async Task 登记时通过HTTPS发送主机标识和公钥但不泄露令牌()
    {
        var (_, publicKey) = Ed25519KeyManager.GenerateKeyPair();
        var config = new AppConfig
        {
            Reporting = new ReportingConfig
            {
                HostId = "7d1cf5b5-1dbe-4cc9-9117-fcededdbdc74",
                ApiBaseUrl = "https://gateway.example/v1"
            }
        };
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri(config.Reporting.ApiBaseUrl) };
        var registrar = new HostKeyRegistrar(config, client, _ => publicKey);

        await registrar.RegisterAsync("token-value");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://gateway.example/v1/hosts/register-key", handler.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(config.Reporting.HostId, body.RootElement.GetProperty("host_id").GetString());
        Assert.Equal(Convert.ToBase64String(publicKey), body.RootElement.GetProperty("public_key").GetString());
        Assert.Equal("token-value", body.RootElement.GetProperty("registration_token").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { host_id = "7d1cf5b5-1dbe-4cc9-9117-fcededdbdc74", registered = true })
            };
        }
    }
}
