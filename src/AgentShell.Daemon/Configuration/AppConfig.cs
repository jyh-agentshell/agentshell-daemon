using Tomlyn;
using Tomlyn.Model;

namespace AgentShell.Daemon.Configuration;

/// <summary>
/// 守护进程完整配置（与 agentshell.toml 对应）
/// </summary>
public sealed record AppConfig
{
    /// <summary>配置文件路径</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentshell",
            "agentshell.toml");

    public MonitorConfig Monitor { get; init; } = new();
    public ReportingConfig Reporting { get; init; } = new();
    public LanConfig Lan { get; init; } = new();
    public BindingConfig Binding { get; init; } = new();
    public LoggingConfig Logging { get; init; } = new();

    /// <summary>
    /// 从默认路径加载并验证配置。配置缺失或无效时拒绝启动。
    /// </summary>
    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;

        // 展开路径中的 ~
        if (path.StartsWith('~'))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.TrimStart('~', '/', '\\'));

        if (!File.Exists(path))
            throw new FileNotFoundException("未找到守护进程配置文件。请先运行 --generate-config 生成配置。", path);

        try
        {
            var toml = File.ReadAllText(path);
            var table = Toml.ToModel(toml);
            var config = FromToml(table);
            config.Validate();
            return config;
        }
        catch (TomlException ex)
        {
            throw new InvalidOperationException("配置文件 TOML 格式无效。", ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("配置文件无效，守护进程拒绝启动。", ex);
        }
    }

    /// <summary>
    /// 验证配置值的合法性。
    /// </summary>
    public void Validate()
    {
        if (Monitor.PollIntervalMs <= 0)
            throw new InvalidOperationException("monitor.poll_interval_ms 必须 > 0");

        if (Reporting.ReportIntervalMs <= 0)
            throw new InvalidOperationException("reporting.report_interval_ms 必须 > 0");

        if (!Guid.TryParse(Reporting.HostId, out _))
            throw new InvalidOperationException("reporting.host_id 必须是有效 UUID");

        if (Lan.Port is < 1 or > 65535)
            throw new InvalidOperationException("lan.port 必须在 1-65535 范围内");

        if (Binding.CodeTtlSeconds <= 0)
            throw new InvalidOperationException("binding.code_ttl_seconds 必须 > 0");
    }

    private static AppConfig FromToml(TomlTable root)
    {
        var config = new AppConfig();

        if (root.TryGetValue("monitor", out var m) && m is TomlTable monitor)
            config = config with { Monitor = MonitorConfig.FromToml(monitor) };

        if (root.TryGetValue("reporting", out var r) && r is TomlTable reporting)
            config = config with { Reporting = ReportingConfig.FromToml(reporting) };

        if (root.TryGetValue("lan", out var l) && l is TomlTable lan)
            config = config with { Lan = LanConfig.FromToml(lan) };

        if (root.TryGetValue("binding", out var b) && b is TomlTable binding)
            config = config with { Binding = BindingConfig.FromToml(binding) };

        if (root.TryGetValue("logging", out var log) && log is TomlTable logging)
            config = config with { Logging = LoggingConfig.FromToml(logging) };

        return config;
    }
}

public sealed record MonitorConfig
{
    public string Type { get; init; } = "tmux";
    public string SessionPattern { get; init; } = "*";
    public int PollIntervalMs { get; init; } = 500;

    internal static MonitorConfig FromToml(TomlTable t)
    {
        var c = new MonitorConfig();
        if (t.TryGetValue("type", out var v)) c = c with { Type = v?.ToString() ?? "tmux" };
        if (t.TryGetValue("session_pattern", out var sp)) c = c with { SessionPattern = sp?.ToString() ?? "*" };
        if (t.TryGetValue("poll_interval_ms", out var pi) && pi is long lpi) c = c with { PollIntervalMs = (int)lpi };
        return c;
    }
}

public sealed record ReportingConfig
{
    public string HostId { get; init; } = string.Empty;
    public string ApiBaseUrl { get; init; } = "https://api.agentshell.dev/v1";
    public int ReportIntervalMs { get; init; } = 1000;

    internal static ReportingConfig FromToml(TomlTable t)
    {
        var c = new ReportingConfig();
        if (t.TryGetValue("host_id", out var hostId)) c = c with { HostId = hostId?.ToString() ?? string.Empty };
        if (t.TryGetValue("api_base_url", out var v)) c = c with { ApiBaseUrl = v?.ToString() ?? c.ApiBaseUrl };
        if (t.TryGetValue("report_interval_ms", out var ri) && ri is long lri) c = c with { ReportIntervalMs = (int)lri };
        return c;
    }
}

public sealed record LanConfig
{
    public bool Enabled { get; init; } = true;
    public int Port { get; init; } = 11920;
    public string BindIp { get; init; } = "";

    internal static LanConfig FromToml(TomlTable t)
    {
        var c = new LanConfig();
        if (t.TryGetValue("enabled", out var v))
        {
            c = v switch
            {
                bool b => c with { Enabled = b },
                string s => c with { Enabled = !s.Equals("false", StringComparison.OrdinalIgnoreCase) },
                _ => c
            };
        }
        if (t.TryGetValue("port", out var p) && p is long lp) c = c with { Port = (int)lp };
        if (t.TryGetValue("bind_ip", out var bip)) c = c with { BindIp = bip?.ToString() ?? "" };
        return c;
    }
}

public sealed record BindingConfig
{
    public string KeyPath { get; init; } = "~/.agentshell/agent.key";
    public int CodeTtlSeconds { get; init; } = 300;

    internal static BindingConfig FromToml(TomlTable t)
    {
        var c = new BindingConfig();
        if (t.TryGetValue("key_path", out var v)) c = c with { KeyPath = v?.ToString() ?? c.KeyPath };
        if (t.TryGetValue("code_ttl_seconds", out var ct) && ct is long lct) c = c with { CodeTtlSeconds = (int)lct };
        return c;
    }
}

public sealed record LoggingConfig
{
    public string Level { get; init; } = "Information";
    public string FilePath { get; init; } = "";

    internal static LoggingConfig FromToml(TomlTable t)
    {
        var c = new LoggingConfig();
        if (t.TryGetValue("level", out var v)) c = c with { Level = v?.ToString() ?? "Information" };
        if (t.TryGetValue("file_path", out var fp)) c = c with { FilePath = fp?.ToString() ?? "" };
        return c;
    }
}
