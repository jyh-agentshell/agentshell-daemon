using System.Security.Cryptography;
using System.Text.Json;

namespace AgentShell.Daemon.Auth;

/// <summary>一次性绑定码的盐化哈希存储；绝不持久化明文绑定码。</summary>
public sealed class BindingCodeStore(string statePath, TimeProvider clock)
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private readonly string _statePath = statePath;
    private readonly TimeProvider _clock = clock;

    public string Generate(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var state = new StoredBindingCode(Convert.ToBase64String(salt), Convert.ToBase64String(Hash(code, salt)), _clock.GetUtcNow().Add(ttl));
        Save(state);
        return code;
    }

    public bool Consume(string code)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return false;
        var state = Load();
        if (state is null || state.ExpiresAt <= _clock.GetUtcNow())
        {
            Delete();
            return false;
        }
        byte[] actual;
        try { actual = Convert.FromBase64String(state.Hash); }
        catch (FormatException) { Delete(); return false; }
        var expected = Hash(code, Convert.FromBase64String(state.Salt));
        var accepted = CryptographicOperations.FixedTimeEquals(expected, actual);
        if (accepted) Delete();
        return accepted;
    }

    private static byte[] Hash(string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(code, salt, 100_000, HashAlgorithmName.SHA256, HashSize);

    private StoredBindingCode? Load()
    {
        if (!File.Exists(_statePath)) return null;
        try { return JsonSerializer.Deserialize<StoredBindingCode>(File.ReadAllText(_statePath)); }
        catch (JsonException) { return null; }
    }

    private void Save(StoredBindingCode state)
    {
        var directory = Path.GetDirectoryName(_statePath) ?? throw new InvalidOperationException("绑定码路径无效");
        var createdDirectory = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (createdDirectory && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = _statePath + ".new";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Delete()
    {
        if (File.Exists(_statePath)) File.Delete(_statePath);
    }

    private sealed record StoredBindingCode(string Salt, string Hash, DateTimeOffset ExpiresAt);
}
