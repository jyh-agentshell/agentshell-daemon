using System.Security.Cryptography;
using System.Text;
using AgentShell.Daemon.Security;
using AgentShell.Protocol.Models;

namespace AgentShell.Daemon.Reporting;

/// <summary>构造 P2.1 状态上报信封并对稳定字节序列签名。</summary>
public sealed class ReportSigner
{
    private readonly byte[] _privateKeySeed;
    private readonly TimeProvider _clock;
    private readonly Func<byte[]> _nonceFactory;

    public ReportSigner(byte[] privateKeySeed, TimeProvider clock, Func<byte[]>? nonceFactory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(privateKeySeed.Length, 32);
        _privateKeySeed = privateKeySeed.ToArray();
        _clock = clock;
        _nonceFactory = nonceFactory ?? (() => RandomNumberGenerator.GetBytes(16));
    }

    public ReportEnvelope Sign(
        string hostId,
        string payloadType,
        byte[] payloadBytes,
        IReadOnlyList<string> capabilities,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadType);
        ArgumentNullException.ThrowIfNull(payloadBytes);
        ArgumentNullException.ThrowIfNull(capabilities);

        var nonce = _nonceFactory();
        if (nonce.Length < 16)
            throw new InvalidOperationException("上报 nonce 至少需要 16 字节。");

        // 线协议时间戳精度固定为毫秒。必须在签名前截断，避免 JSON 转换器序列化后
        // 改变时间戳文本，导致服务端使用收到的信封验签失败。
        var sourceTimestamp = timestamp ?? _clock.GetUtcNow();
        var emittedAt = DateTimeOffset.FromUnixTimeMilliseconds(sourceTimestamp.ToUnixTimeMilliseconds());
        var envelope = new ReportEnvelope(
            "0.3.1",
            hostId,
            emittedAt,
            Convert.ToBase64String(nonce),
            capabilities,
            payloadType,
            Convert.ToBase64String(payloadBytes),
            Convert.ToHexStringLower(SHA256.HashData(payloadBytes)),
            string.Empty);
        var signature = Ed25519KeyManager.Sign(_privateKeySeed, BuildSignedBytes(envelope));
        return envelope with { Signature = Convert.ToBase64String(signature) };
    }

    public static byte[] BuildSignedBytes(ReportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Encoding.UTF8.GetBytes(string.Join('\n',
            envelope.ProtocolVersion,
            envelope.HostId,
            ProtocolTimestamp.Format(envelope.Timestamp),
            envelope.Nonce,
            envelope.PayloadType,
            envelope.PayloadSha256));
    }
}
