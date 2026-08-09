namespace AgentShell.Daemon.Auth;

/// <summary>
/// Access Token 的本地持久化存储。
/// 文件路径: ~/.agentshell/access_token，权限 0600。
/// </summary>
public sealed class TokenStore
{
    private readonly string _tokenPath;

    public TokenStore(string? tokenPath = null)
    {
        _tokenPath = tokenPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentshell",
            "access_token");
    }

    /// <summary>
    /// 异步保存 Token 到磁盘。
    /// </summary>
    public async Task SaveAsync(string token, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_tokenPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(_tokenPath, token, ct);

        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // 权限设置失败不阻塞
        }
    }

    /// <summary>
    /// 异步从磁盘加载 Token。文件不存在返回 null。
    /// </summary>
    public Task<string?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_tokenPath))
            return Task.FromResult<string?>(null);

        var token = File.ReadAllText(_tokenPath).Trim();
        return Task.FromResult(string.IsNullOrEmpty(token) ? null : token)!;
    }
}
