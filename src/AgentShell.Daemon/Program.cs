using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentShell.Daemon.Auth;
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

                // Token 生命周期管理（绑定完成后由外部注入首个 Token）
                services.AddSingleton<TokenStore>();
                services.AddSingleton<TokenManager>(sp =>
                {
                    var config = sp.GetRequiredService<AppConfig>();
                    var store = sp.GetRequiredService<TokenStore>();
                    var logger = sp.GetRequiredService<ILogger<TokenManager>>();
                    var tm = new TokenManager(config, store, logger);
                    // 同步阻塞初始化（守护进程启动时必须完成）
                    tm.InitializeAsync().GetAwaiter().GetResult();
                    return tm;
                });

                // 上报客户端（真实 HTTPS 上报，通过 TokenManager 获取 Bearer Token）
                services.AddSingleton<IApiReporter, HttpApiReporter>();

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
                if (args.Length > 2)
                {
                    Console.Error.WriteLine("--generate-binding-code 最多接受一个 SSH 主机名参数");
                    return 1;
                }
                return GenerateBindingCode(args.ElementAtOrDefault(1));
            case "--version":
            case "-v":
                var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.2.0";
                Console.WriteLine($"agentshell-daemon v{version}");
                return 0;
            case "bind-verify":
                return HandleBindVerify(args);
            case "register-key":
                return HandleRegisterKey();
            case "--set-token":
                return HandleSetToken();
            default:
                Console.Error.WriteLine($"未知命令: {args[0]}");
                Console.Error.WriteLine("可用命令: --generate-config | --generate-binding-code | bind-verify | register-key | --set-token | --version");
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
full_sync_interval_seconds = 30

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

    private static int GenerateBindingCode(string? sshHostname)
    {
        try
        {
            var config = AppConfig.Load();
            var code = CreateBindingStore(config).Generate(TimeSpan.FromSeconds(config.Binding.CodeTtlSeconds));
            if (string.IsNullOrWhiteSpace(sshHostname))
            {
                Console.WriteLine(code);
                return 0;
            }

            if (sshHostname.Any(char.IsWhiteSpace) || sshHostname.Any(char.IsControl))
                throw new ArgumentException("SSH 主机名不能包含空白或控制字符。");
            Console.WriteLine($"agentshell://bind?code={code}&host={Uri.EscapeDataString(sshHostname)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"生成绑定码失败: {ex.Message}");
            return 1;
        }
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

            var delimiter = input.IndexOf(':');
            if (delimiter != 6 || input.Length == delimiter + 1 || !CreateBindingStore(AppConfig.Load()).Consume(input[..delimiter]))
            {
                Console.Error.WriteLine("绑定码无效、已过期或已被使用");
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

    /// <summary>
    /// 执行 --set-token CLI 子命令。
    /// 从 stdin 读取一行 Access Token（App 绑定完成后通过 SSH 管道注入），
    /// 用 TokenStore 保存到 ~/.agentshell/access_token。
    /// </summary>
    private static int HandleSetToken()
    {
        try
        {
            var token = Console.In.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine("错误：未提供 Token（从 stdin 读取为空）");
                return 1;
            }

            var store = new TokenStore();
            store.SaveAsync(token).GetAwaiter().GetResult();

            Console.WriteLine("Token 已保存到 ~/.agentshell/access_token");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"保存 Token 失败: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 执行 register-key CLI 子命令。
    /// 从 stdin 接收 App 刚取得的一次性登记令牌，向 HTTPS 网关登记 daemon 公钥。
    /// </summary>
    private static int HandleRegisterKey()
    {
        try
        {
            var token = Console.In.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine("错误：未提供主机登记令牌");
                return 1;
            }

            var config = AppConfig.Load();
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var registrar = new HostKeyRegistrar(config, httpClient);
            registrar.RegisterAsync(token).GetAwaiter().GetResult();
            Console.WriteLine("主机公钥已登记");
            return 0;
        }
        catch (Exception ex)
        {
            // 不得将一次性令牌写入 stderr 或日志。
            Console.Error.WriteLine($"主机公钥登记失败: {ex.Message}");
            return 1;
        }
    }

    private static BindingCodeStore CreateBindingStore(AppConfig config)
    {
        var stateDirectory = Path.GetDirectoryName(AppConfig.DefaultPath) ?? throw new InvalidOperationException("配置目录无效");
        return new BindingCodeStore(Path.Combine(stateDirectory, "binding-code.state"), TimeProvider.System);
    }
}
