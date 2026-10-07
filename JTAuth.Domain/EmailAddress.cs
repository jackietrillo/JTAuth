using System.Buffers;

namespace JTAuth.Domain;

/// <summary>
/// The one form an email address takes everywhere it is stored or compared: trimmed and lower-cased.
/// Two spellings of the same address therefore reach the same sign-in identity and cannot make two accounts.
/// </summary>
public static class EmailAddress
{
    public const int MaxLength = 254;

    private const int MaxLocalPartLength = 64;

    private static readonly SearchValues<char> ForbiddenCharacters = SearchValues.Create(" \t\r\n,;:<>()[]\\\"");

    /// <summary>Trims and lower-cases an address (culture independent). Does not check that it is valid.</summary>
    public static string Normalize(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes an address and says whether the result looks like one mailbox: a single '@', a local part and a
    /// dotted domain, no spaces, quotes, brackets or list separators. Whether the mailbox exists is proved by the code.
    /// </summary>
    public static bool TryNormalize(string? address, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var candidate = Normalize(address);
        if (candidate.Length > MaxLength || candidate.AsSpan().ContainsAny(ForbiddenCharacters) || candidate.Any(char.IsControl))
        {
            return false;
        }

        var at = candidate.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != candidate.LastIndexOf('@') || at > MaxLocalPartLength)
        {
            return false;
        }

        var domain = candidate[(at + 1)..];
        var labels = domain.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length == 0 || label.StartsWith('-') || label.EndsWith('-')))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
