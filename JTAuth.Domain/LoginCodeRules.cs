using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JTAuth.Domain;

/// <summary>How one-time sign-in codes are made and stored.</summary>
public static class LoginCodeRules
{
    public const int CodeLength = 6;

    public const int MaxAttempts = 5;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>A uniformly random code of <see cref="CodeLength"/> digits, with leading zeros kept.</summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D" + CodeLength, CultureInfo.InvariantCulture);

    /// <summary>True for exactly <see cref="CodeLength"/> ASCII digits.</summary>
    public static bool IsWellFormed(string? code) =>
        code is { Length: CodeLength } && code.All(char.IsAsciiDigit);

    /// <summary>
    /// The value stored in place of a code: HMAC-SHA256 over the provider, the address and the code, keyed with a server
    /// secret. A million possible codes would fall to a plain hash in an instant if the table leaked; without the secret
    /// the hash cannot be searched, and it only fits the address it was made for.
    /// </summary>
    public static byte[] Hash(string secret, IdentityProvider provider, string providerSubject, string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(providerSubject);
        ArgumentNullException.ThrowIfNull(code);

        var payload = Encoding.UTF8.GetBytes($"{provider}\n{providerSubject}\n{code}");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
    }
}
