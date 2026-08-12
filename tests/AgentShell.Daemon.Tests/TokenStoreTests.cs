using AgentShell.Daemon.Auth;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class TokenStoreTests
{
    [Fact]
    public async Task 保存Token使用原子替换并设置私有权限()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentshell-token-{Guid.NewGuid():N}");
        try
        {
            var store = new TokenStore(path);
            await store.SaveAsync("secret-token");

            Assert.Equal("secret-token", await store.LoadAsync());
            Assert.False(File.Exists(path + ".new"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".new")) File.Delete(path + ".new");
        }
    }
}
