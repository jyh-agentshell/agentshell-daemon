using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Monitors;
using AgentShell.Daemon.Reporting;
using AgentShell.Daemon.Services;

namespace AgentShell.Daemon;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                // 配置系统
                var config = AppConfig.Load();
                services.AddSingleton(config);

                // 监控目标（根据配置选择复用器）
                services.AddSingleton<IMonitorTarget>(sp =>
                {
                    var cfg = sp.GetRequiredService<AppConfig>();
                    return cfg.Monitor.Type switch
                    {
                        "zellij" => throw new NotSupportedException("Zellij 监控尚未实现"),
                        "screen" => throw new NotSupportedException("Screen 监控尚未实现"),
                        "pty" => throw new NotSupportedException("PTY 监控尚未实现"),
                        _ => new TmuxMonitor(sp.GetRequiredService<ILogger<TmuxMonitor>>(), cfg)
                    };
                });

                // 上报客户端（Phase 2 实现真实 HTTP 客户端）
                services.AddSingleton<IApiReporter, NoOpReporter>();

                // 守护进程核心服务
                services.AddHostedService<DaemonService>();
            })
            .Build();

        await host.RunAsync();
    }
}
