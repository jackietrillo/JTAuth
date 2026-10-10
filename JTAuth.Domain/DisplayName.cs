namespace JTAuth.Domain;

/// <summary>The name a person goes by in apps: shown to others, carried in the access token's <c>name</c> claim.</summary>
public static class DisplayName
{
    public const int MaxLength = 50;

    /// <summary>
    /// Trims the name and says whether it is acceptable: one to <see cref="MaxLength"/> characters, on a single line, with no
    /// control characters (they would break logs and layouts).
    /// </summary>
    public static bool TryNormalize(string? name, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = name.Trim();
        if (candidate.Length > MaxLength || candidate.Any(char.IsControl))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
