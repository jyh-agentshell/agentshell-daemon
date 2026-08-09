using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace AgentShell.Daemon.Security;

/// <summary>
/// Ed25519 密钥管理（RFC 8032）。
/// 实现说明：.NET BCL 截至 .NET 10 未公开独立的 System.Security.Cryptography.Ed25519 类
/// （仅 ML-DSA 复合算法的常量中提及 Ed25519），因此使用 BouncyCastle.Cryptography
/// 的纯托管 RFC 8032 实现，兼容 PublishTrimmed。
/// 密钥格式：seed(32) + publicKey(32)，与协议层约定的 Ed25519 原始字节一致。
/// </summary>
public static class Ed25519KeyManager
{
    private const int PrivateKeySeedSize = 32;
    private const int PublicKeySize = 32;
    private const int SignatureSize = 64;

    /// <summary>
    /// 生成新的 Ed25519 密钥对。
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var seed = new byte[PrivateKeySeedSize];
        RandomNumberGenerator.Fill(seed);

        var publicKey = new byte[PublicKeySize];
        Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);

        return (seed, publicKey);
    }

    /// <summary>
    /// 从文件加载已有密钥对，如不存在则生成新密钥对并持久化。
    /// 文件格式: 前 32 字节为私钥 seed，后 32 字节为公钥。
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKey) LoadOrCreateKeyPair(string keyPath)
    {
        keyPath = ExpandPath(keyPath);

        if (File.Exists(keyPath))
        {
            var data = File.ReadAllBytes(keyPath);
            if (data.Length != PrivateKeySeedSize + PublicKeySize)
                throw new InvalidOperationException($"密钥文件 {keyPath} 格式无效（期望 64 字节）");

            var priv = data[..PrivateKeySeedSize];
            var pub = data[PrivateKeySeedSize..];
            return (priv, pub);
        }

        var (privateKey, publicKey) = GenerateKeyPair();

        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var combined = new byte[PrivateKeySeedSize + PublicKeySize];
        Buffer.BlockCopy(privateKey, 0, combined, 0, PrivateKeySeedSize);
        Buffer.BlockCopy(publicKey, 0, combined, PrivateKeySeedSize, PublicKeySize);
        File.WriteAllBytes(keyPath, combined);

        // 设置权限 0600（Unix）；Windows 上为当前用户独占
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // 权限设置失败不阻塞启动
        }

        return (privateKey, publicKey);
    }

    /// <summary>
    /// 使用 Ed25519 私钥签名消息。
    /// </summary>
    public static byte[] Sign(byte[] privateKeySeed, byte[] message)
    {
        var signature = new byte[SignatureSize];
        Ed25519.Sign(privateKeySeed, 0, message, 0, message.Length, signature, 0);
        return signature;
    }

    /// <summary>
    /// 验证 Ed25519 签名。
    /// </summary>
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将公钥导出为 Base64 字符串。
    /// </summary>
    public static string ExportPublicKey(byte[] publicKey)
        => Convert.ToBase64String(publicKey);

    /// <summary>
    /// 展开路径中的 ~ 为用户主目录。
    /// </summary>
    private static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.TrimStart('~', '/', '\\'));
        return path;
    }
}
