using AgentShell.Daemon.Reporting;
using AgentShell.Daemon.Security;
using AgentShell.Protocol.Models;
using Xunit;

namespace AgentShell.Daemon.Tests;

public sealed class ReportSignerTests
{
    [Theory]
    [InlineData("https://api.example/v1", true)]
    [InlineData("http://api.example/v1", false)]
    [InlineData("ftp://api.example/v1", false)]
    public void 上报地址必须使用HTTPS(string value, bool expected) =>
        Assert.Equal(expected, HttpApiReporter.IsSecureApiBaseUrl(value));

    [Fact]
    public void Sign_生成可由主机公钥验证的规范Envelope()
    {
        var seed = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var publicKey = new byte[32];
        Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);
        var signer = new ReportSigner(
            seed,
            TimeProvider.System,
            () => Enumerable.Repeat((byte)7, 16).ToArray());

        var envelope = signer.Sign(
            "11111111-1111-4111-8111-111111111111",
            "agent_state",
            "{}"u8.ToArray(),
            ["agent_state"],
            DateTimeOffset.Parse("2026-08-11T00:00:00Z"));

        Assert.Equal("0.3.1", envelope.ProtocolVersion);
        Assert.Equal("2026-08-11T00:00:00.000Z", ProtocolTimestamp.Format(envelope.Timestamp));
        Assert.Equal(Convert.ToBase64String(Enumerable.Repeat((byte)7, 16).ToArray()), envelope.Nonce);
        Assert.True(Ed25519KeyManager.Verify(
            publicKey,
            ReportSigner.BuildSignedBytes(envelope),
            Convert.FromBase64String(envelope.Signature)));
    }

    [Fact]
    public void Sign_签名前将时间截断为线协议毫秒精度()
    {
        var signer = new ReportSigner(
            Enumerable.Repeat((byte)1, 32).ToArray(),
            TimeProvider.System,
            () => Enumerable.Repeat((byte)2, 16).ToArray());

        var envelope = signer.Sign(
            "11111111-1111-4111-8111-111111111111",
            "agent_state",
            "{}"u8.ToArray(),
            ["agent_state"],
            new DateTimeOffset(2026, 8, 11, 0, 0, 0, 123, TimeSpan.Zero).AddTicks(4567));

        Assert.Equal("2026-08-11T00:00:00.123Z", ProtocolTimestamp.Format(envelope.Timestamp));
    }
}
