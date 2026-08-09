using AgentShell.Daemon.Security;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class Ed25519KeyManagerTests : IDisposable
{
    private readonly string _testDir;

    public Ed25519KeyManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"agentshell-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void GenerateKeyPair_生成32字节私钥和32字节公钥()
    {
        var (privateKey, publicKey) = Ed25519KeyManager.GenerateKeyPair();

        Assert.Equal(32, privateKey.Length);
        Assert.Equal(32, publicKey.Length);
    }

    [Fact]
    public void Sign_签名可被对应公钥验证()
    {
        var (privateKey, publicKey) = Ed25519KeyManager.GenerateKeyPair();
        var message = "123456:abc123nonce"u8.ToArray();

        var signature = Ed25519KeyManager.Sign(privateKey, message);

        // 使用 .NET 内置 Ed25519 验证签名
        Assert.True(Ed25519KeyManager.Verify(publicKey, message, signature));
    }

    [Fact]
    public void LoadOrCreateKeyPair_文件不存在时创建并持久化()
    {
        var keyPath = Path.Combine(_testDir, "agent.key");
        Assert.False(File.Exists(keyPath));

        var (privateKey, publicKey) = Ed25519KeyManager.LoadOrCreateKeyPair(keyPath);

        Assert.True(File.Exists(keyPath));
        Assert.Equal(32, privateKey.Length);
        Assert.Equal(32, publicKey.Length);
    }

    [Fact]
    public void LoadOrCreateKeyPair_文件已存在时加载()
    {
        var keyPath = Path.Combine(_testDir, "agent.key");
        var (originalPriv, originalPub) = Ed25519KeyManager.LoadOrCreateKeyPair(keyPath);

        var (loadedPriv, loadedPub) = Ed25519KeyManager.LoadOrCreateKeyPair(keyPath);

        Assert.Equal(originalPriv, loadedPriv);
        Assert.Equal(originalPub, loadedPub);
    }

    [Fact]
    public void ExportPublicKey_返回Base64公钥()
    {
        var (_, publicKey) = Ed25519KeyManager.GenerateKeyPair();
        var exported = Ed25519KeyManager.ExportPublicKey(publicKey);

        var decoded = Convert.FromBase64String(exported);
        Assert.Equal(32, decoded.Length);
    }
}
