using System.Security.Cryptography;
using System.Text;

namespace JTAuth.Domain;

/// <summary>
/// The key an app's server uses to call JTAuth on its own behalf. Only the SHA-256 of the key is stored (<c>Client.ServiceKeyHash</c>),
/// so the database never holds a key that works. The keys are long random values, which is why a fast hash is enough.
/// </summary>
public static class ServiceKeyRules
{
    /// <summary>The value stored for a key, or null when the key is empty.</summary>
    public static byte[]? Hash(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()));
}