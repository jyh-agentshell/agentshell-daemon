using AgentShell.Daemon.Monitors;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class MonitorTrustBoundaryTests
{
    [Fact]
    public void P2监控接口不暴露远程按键执行() =>
        Assert.DoesNotContain(typeof(IMonitorTarget).GetMethods(), method =>
            method.Name.Contains("SendKeys", StringComparison.Ordinal));
}
