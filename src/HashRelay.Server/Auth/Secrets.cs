using System.Security.Cryptography;
using System.Text;

namespace HashRelay.Server.Auth;

/// <summary>Secret generation and hashing. Credentials are stored as SHA-256 verifiers; passwords as PBKDF2.</summary>
public static class Secrets
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const string ApiKeyPrefix = "hr_live_";
    public const string DeviceTokenPrefix = "hr_dev_";
    public const string ServiceKeyPrefix = "hr_svc_";
    private static readonly string[] Prefixes = [ApiKeyPrefix, DeviceTokenPrefix, ServiceKeyPrefix];

    public static string NewApiKey() => ApiKeyPrefix + RandomString(40);
    public static string NewDeviceToken() => DeviceTokenPrefix + RandomString(40);
    public static string NewServiceKey() => ServiceKeyPrefix + RandomString(40);
    public static string NewDeviceCode() => RandomString(48);

    /// <summary>Human-friendly pairing code, e.g. "KQ7M-3PXD" (no ambiguous characters).</summary>
    public static string NewUserCode()
    {
        const string safe = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[8];
        for (var i = 0; i < 8; i++) chars[i] = safe[RandomNumberGenerator.GetInt32(safe.Length)];
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
    }

    public static string RandomString(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Hint(string secret)
    {
        var prefix = Prefixes.FirstOrDefault(secret.StartsWith) ?? "";
        return prefix + "…" + secret[^4..];
    }

    public static bool LooksLikeSecret(string? value)
        => value is not null && Prefixes.Any(value.StartsWith) && value.Length >= 20;

    /// <summary>A random temporary password for operator resets: 16 unambiguous letters and digits.</summary>
    public static string TemporaryPassword() => RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 16);

    // ---- passwords: PBKDF2-SHA256, format "pbkdf2$<iterations>$<salt-b64>$<hash-b64>" ----
    private const int Iterations = 210_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
