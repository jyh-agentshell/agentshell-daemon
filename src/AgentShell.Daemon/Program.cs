using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentShell.Daemon.Configuration;
using AgentShell.Daemon.Monitors;
using AgentShell.Daemon.Reporting;
using AgentShell.Daemon.Security;
using AgentShell.Daemon.Services;

namespace AgentShell.Daemon;

public static class Program
{
    /// <summary>
    /// 主入口。支持两种模式：
    /// 1. CLI 子命令（--generate-config / --version）→ 执行后立即退出
    /// 2. 无参数 → 启动守护进程（IHostedService）
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
            return HandleCliCommand(args);

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
        return 0;
    }

    /// <summary>
    /// 处理 CLI 子命令（给 install.sh 和手动运维使用）。
    /// 这些命令不启动守护进程，执行后立即退出。
    /// </summary>
    private static int HandleCliCommand(string[] args)
    {
        switch (args[0])
        {
            case "--generate-config":
                GenerateConfig();
                return 0;
            case "--generate-binding-code":
                return BindingNotImplemented();
            case "--version":
            case "-v":
                var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.2.0";
                Console.WriteLine($"agentshell-daemon v{version}");
                return 0;
            case "bind-verify":
                return HandleBindVerify(args);
            default:
                Console.Error.WriteLine($"未知命令: {args[0]}");
                Console.Error.WriteLine("可用命令: --generate-config | --version");
                return 1;
        }
    }

    /// <summary>
    /// 输出默认 TOML 配置到 stdout（install.sh 用它生成 ~/.agentshell/agentshell.toml）。
    /// </summary>
    private static void GenerateConfig()
    {
        Console.WriteLine($@"# AgentShell 守护进程配置
# 由 install.sh 自动生成

[monitor]
# 终端复用器类型: tmux | screen | zellij | pty
type = ""tmux""
# 监控的会话名 glob 模式
session_pattern = ""*""
# 轮询间隔（毫秒）
poll_interval_ms = 500

[reporting]
# 此 UUID 是本机唯一身份；请勿与其他主机复用
host_id = ""{Guid.NewGuid()}""
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

    private static int BindingNotImplemented()
    {
        Console.Error.WriteLine("设备绑定尚未实现；未生成绑定码或签名。守护进程拒绝伪造服务端绑定结果。");
        return 1;
    }

    /// <summary>
    /// 执行 bind-verify CLI 子命令。
    /// 从 stdin 读取 "{binding_code}:{nonce}"，用 Ed25519 私钥签名，
    /// 输出 JSON {host_id, signature, public_key} 到 stdout。
    /// host_id 来自 agentshell.toml 的 reporting.host_id，供 App 关联主机公钥。
    /// </summary>
    private static int HandleBindVerify(string[] args)
    {
        try
        {
            // 从 stdin 读取要签名的消息
            var input = Console.In.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(input))
            {
                Console.Error.WriteLine("bind-verify 需要从 stdin 读取 \"{binding_code}:{nonce}\"");
                return 1;
            }

            // 加载或创建密钥对
            var config = AppConfig.Load();
            var keyPath = config.Binding.KeyPath;
            var (privateKey, publicKey) = Ed25519KeyManager.LoadOrCreateKeyPair(keyPath);

            // 签名
            var message = System.Text.Encoding.UTF8.GetBytes(input);
            var signature = Ed25519KeyManager.Sign(privateKey, message);

            // 输出 JSON 到 stdout
            var result = $$"""
            {
              "host_id": "{{config.Reporting.HostId}}",
              "signature": "{{Convert.ToBase64String(signature)}}",
              "public_key": "{{Convert.ToBase64String(publicKey)}}"
            }
            """;
            Console.WriteLine(result);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bind-verify 失败: {ex.Message}");
            return 1;
        }
    }
}
