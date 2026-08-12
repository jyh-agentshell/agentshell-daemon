using System.Reflection;
using System.Text.Json;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Monitors;
using AgentShell.Daemon.Reporting;
using AgentShell.Daemon.Services;
using AgentShell.Protocol.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class DaemonServiceTests
{
    [Fact]
    public async Task TickAsync_为每个匹配会话使用配置HostId上报且不携带OSC终端原文()
    {
        const string hostId = "9e01c440-8e39-4b84-9af7-67455467a837";
        const string alphaPane = "\u001b]9;agent_state=awaiting_approval;files=3;prompt=U2VjcmV0IHByb21wdA==;message=U2VjcmV0IG1lc3NhZ2U=\a";
        var reporter = new RecordingReporter();
        var monitor = new FakeMonitor(new Dictionary<string, string>
        {
            ["alpha"] = alphaPane,
            ["beta"] = "\u001b]9;agent_state=awaiting_approval;files=2;prompt=QW5vdGhlciBzZWNyZXQ=\a"
        });
        var service = new DaemonService(
            monitor,
            reporter,
            new AppConfig
            {
                Reporting = new ReportingConfig { HostId = hostId }
            },
            NullLogger<DaemonService>.Instance);

        Assert.NotNull(TryParseOscMarker(service, alphaPane));
        await RunOneTickAsync(service);

        Assert.Equal(2, reporter.StateEvents.Count);
        Assert.Equal(2, reporter.LifecycleEvents.Count);
        Assert.All(reporter.StateEvents, evt => Assert.StartsWith(hostId + "/tmux/", evt.SessionId, StringComparison.Ordinal));
        Assert.All(reporter.LifecycleEvents, evt => Assert.StartsWith(hostId + "/tmux/", evt.SessionId, StringComparison.Ordinal));
        Assert.All(reporter.StateEvents, evt => Assert.NotNull(evt.Detail));
        Assert.Equal(3, reporter.StateEvents.Single(evt => evt.SessionId.EndsWith("/alpha", StringComparison.Ordinal)).Detail!.FileCount);
        var reportedJson = JsonSerializer.Serialize(reporter.StateEvents);
        Assert.DoesNotContain("Secret prompt", reportedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret message", reportedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_报告HostId无效时安全失败()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentshell-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, "[reporting]\nhost_id = \"not-a-uuid\"\n");

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => AppConfig.Load(path));
            Assert.Contains("reporting.host_id", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TickAsync_一个会话捕获失败时仍上报其后的会话()
    {
        var reporter = new RecordingReporter();
        var service = CreateService(
            new ThrowingCaptureMonitor(
                "alpha",
                new Dictionary<string, string>
                {
                    ["alpha"] = "",
                    ["beta"] = "\u001b]9;agent_state=awaiting_approval;files=1\a"
                }),
            reporter);

        await RunOneTickAsync(service);

        var reported = Assert.Single(reporter.StateEvents);
        Assert.EndsWith("/beta", reported.SessionId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TickAsync_首次上报失败后下一次轮询会重试相同状态()
    {
        var reporter = new FlakyReporter(failedAttempts: 1);
        var service = CreateService(
            new FakeMonitor(new Dictionary<string, string>
            {
                ["alpha"] = "\u001b]9;agent_state=awaiting_approval;files=1\a"
            }),
            reporter);

        await RunOneTickAsync(service);
        await RunOneTickAsync(service);

        Assert.Equal(2, reporter.StateReportAttempts);
        Assert.Single(reporter.StateEvents);
    }

    private static DaemonService CreateService(IMonitorTarget monitor, IApiReporter reporter) =>
        new(
            monitor,
            reporter,
            new AppConfig
            {
                Reporting = new ReportingConfig { HostId = "9e01c440-8e39-4b84-9af7-67455467a837" }
            },
            NullLogger<DaemonService>.Instance);

    private static async Task RunOneTickAsync(DaemonService service)
    {
        var method = typeof(DaemonService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;
    }

    private static object? TryParseOscMarker(DaemonService service, string content)
    {
        var method = typeof(DaemonService).GetMethod("TryParseOscMarker", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(service, [content]);
    }

    private sealed class FakeMonitor(IReadOnlyDictionary<string, string> panes) : IMonitorTarget
    {
        public string Type => "tmux";
        public bool IsHealthy => true;

        public Task<IReadOnlyList<string>> GetSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(panes.Keys.OrderBy(name => name).ToArray());

        public Task<string> CapturePaneAsync(string sessionName, CancellationToken ct = default) =>
            Task.FromResult(panes[sessionName]);

        public Task SendKeysAsync(string sessionName, string keys, CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingCaptureMonitor(string failedSession, IReadOnlyDictionary<string, string> panes) : IMonitorTarget
    {
        public string Type => "tmux";
        public bool IsHealthy => true;

        public Task<IReadOnlyList<string>> GetSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(panes.Keys.OrderBy(name => name).ToArray());

        public Task<string> CapturePaneAsync(string sessionName, CancellationToken ct = default)
        {
            if (string.Equals(sessionName, failedSession, StringComparison.Ordinal))
                throw new InvalidOperationException("模拟会话捕获失败");

            return Task.FromResult(panes[sessionName]);
        }

        public Task SendKeysAsync(string sessionName, string keys, CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingReporter : IApiReporter
    {
        public List<AgentStateEvent> StateEvents { get; } = [];
        public List<SessionLifecycleEvent> LifecycleEvents { get; } = [];

        public Task<ReportResult> ReportAgentStateAsync(AgentStateEvent stateEvent, CancellationToken ct = default)
        {
            StateEvents.Add(stateEvent);
            return Task.FromResult(ReportResult.Accepted);
        }

        public Task<ReportResult> ReportSessionLifecycleAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken ct = default)
        {
            LifecycleEvents.Add(lifecycleEvent);
            return Task.FromResult(ReportResult.Accepted);
        }

        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FlakyReporter(int failedAttempts) : IApiReporter
    {
        private int _remainingFailures = failedAttempts;

        public int StateReportAttempts { get; private set; }
        public List<AgentStateEvent> StateEvents { get; } = [];

        public Task<ReportResult> ReportAgentStateAsync(AgentStateEvent stateEvent, CancellationToken ct = default)
        {
            StateReportAttempts++;
            if (_remainingFailures-- > 0)
                return Task.FromResult(ReportResult.RetryableFailure);

            StateEvents.Add(stateEvent);
            return Task.FromResult(ReportResult.Accepted);
        }

        public Task<ReportResult> ReportSessionLifecycleAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken ct = default) => Task.FromResult(ReportResult.Accepted);

        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
