using AgentShell.Daemon.Auth;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class BindingCodeStoreTests
{
    [Fact]
    public void 绑定码仅可消费一次且状态文件不含明文()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentshell-binding-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BindingCodeStore(path, TimeProvider.System);
            var code = store.Generate(TimeSpan.FromMinutes(5));

            Assert.Matches("^[0-9]{6}$", code);
            Assert.DoesNotContain(code, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.True(store.Consume(code));
            Assert.False(store.Consume(code));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
