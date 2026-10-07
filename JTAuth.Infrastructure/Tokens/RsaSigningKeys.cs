using System.Security.Cryptography;
using System.Text.Json;
using JTAuth.Application;
using Microsoft.IdentityModel.Tokens;

namespace JTAuth.Infrastructure.Tokens;

/// <summary>
/// The RSA key access tokens are signed with (RS256), kept in a file outside the repository. The file is created on first
/// use and never committed. The key id is the SHA-256 of the public key, so it is the same every time the file is loaded.
/// </summary>
public sealed class RsaSigningKeys : ISigningKeySource
{
    private const int KeySizeBits = 2048;

    private RsaSigningKeys(RsaSecurityKey key)
    {
        Credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
        PublishedKeys =
        [
            new PublicSigningKey(key.KeyId, SecurityAlgorithms.RsaSha256, Base64UrlEncoder.Encode(parameters.Modulus), Base64UrlEncoder.Encode(parameters.Exponent)),
        ];
    }

    /// <summary>What tokens are signed with.</summary>
    public SigningCredentials Credentials { get; }

    public IReadOnlyList<PublicSigningKey> PublishedKeys { get; }

    /// <summary>Loads the key from the file, creating the file with a new key if it does not exist.</summary>
    public static RsaSigningKeys LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            Create(path);
        }

        var stored = JsonSerializer.Deserialize<StoredKey>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"The signing key file '{path}' is empty.");

        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.PrivateKeyPkcs8), out _);

        return new RsaSigningKeys(new RsaSecurityKey(rsa) { KeyId = KeyIdOf(rsa) });
    }

    private static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var rsa = RSA.Create(KeySizeBits);
        var json = JsonSerializer.Serialize(new StoredKey(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())));

        try
        {
            // CreateNew: if another process created the file a moment ago, keep theirs instead of overwriting it.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
    }

    private static string KeyIdOf(RSA rsa) => Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

    private sealed record StoredKey(string PrivateKeyPkcs8);
}
