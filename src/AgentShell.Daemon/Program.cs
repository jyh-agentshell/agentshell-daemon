using System.Security.Cryptography;
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
    /// <summary>
    /// 主入口。支持两种模式：
    /// 1. CLI 子命令（--generate-config / --generate-binding-code / bind-verify）→ 执行后立即退出
    /// 2. 无参数 → 启动守护进程（IHostedService）
    /// </summary>
    public static async Task Main(string[] args)
    {
        if (args.Length > 0)
        {
            HandleCliCommand(args);
            return;
        }

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

    /// <summary>
    /// 处理 CLI 子命令（给 install.sh 和手动运维使用）。
    /// 这些命令不启动守护进程，执行后立即退出。
    /// </summary>
    private static void HandleCliCommand(string[] args)
    {
        switch (args[0])
        {
            case "--generate-config":
                GenerateConfig();
                break;
            case "--generate-binding-code":
                GenerateBindingCode();
                break;
            case "--version":
            case "-v":
                var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.2.0";
                Console.WriteLine($"agentshell-daemon v{version}");
                break;
            case "bind-verify":
                if (args.Length > 1)
                    BindVerify(args[1]);
                else
                    Console.Error.WriteLine("用法: agentshell-daemon bind-verify <nonce>");
                break;
            default:
                Console.Error.WriteLine($"未知命令: {args[0]}");
                Console.Error.WriteLine("可用命令: --generate-config | --generate-binding-code | --version | bind-verify <nonce>");
                break;
        }
    }

    /// <summary>
    /// 输出默认 TOML 配置到 stdout（install.sh 用它生成 ~/.agentshell/agentshell.toml）。
    /// </summary>
    private static void GenerateConfig()
    {
        Console.WriteLine(@"# AgentShell 守护进程配置
# 由 install.sh 自动生成

[monitor]
# 终端复用器类型: tmux | screen | zellij | pty
type = ""tmux""
# 监控的会话名 glob 模式
session_pattern = ""*""
# 轮询间隔（毫秒）
poll_interval_ms = 500

[reporting]
# .NET 网关 API 地址
api_base_url = ""https://api.agentshell.dev/v1""
# 上报间隔（毫秒）
report_interval_ms = 1000

[lan]
# 是否启用局域网直连模式
enabled = true
# 内嵌 Kestrel 监听端口
port = 11920
# 绑定的网络接口（空 = 自动检测局域网 IP + 127.0.0.1）
bind_ip = """"

[binding]
# Ed25519 密钥对存储路径
key_path = ""~/.agentshell/agent.key""
# 绑定码有效期（秒）
code_ttl_seconds = 300

[logging]
# 日志级别: Trace | Debug | Information | Warning | Error | Critical
level = ""Information""
# 日志文件路径（空 = 仅控制台输出）
file_path = ""~/.agentshell/daemon.log""");
    }

    /// <summary>
    /// 生成 6 位随机数字绑定码并输出到 stdout。
    /// install.sh 用它获取初始绑定码供用户扫码。
    /// </summary>
    private static void GenerateBindingCode()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000);
        Console.WriteLine(code.ToString("D6"));
    }

    /// <summary>
    /// Ed25519 签名验证绑定（Phase 2 占位）。
    /// 当前仅打印占位签名，Phase 2 实装 Ed25519 密钥加载和签名。
    /// </summary>
    private static void BindVerify(string nonce)
    {
        // Phase 2: 加载 ~/.agentshell/agent.key → 用 Ed25519 签名 nonce → 输出 base64 签名
        Console.WriteLine($"placeholder-signature-{nonce}");
    }
}
