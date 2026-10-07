namespace JTAuth.Application;

/// <summary>Configuration read from the <c>JTAuth</c> section.</summary>
public sealed class JTAuthSettings
{
    public const string SectionName = "JTAuth";

    /// <summary>The API's public base URL: the token <c>iss</c>, and where the published keys are found.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The server secret that keys the hash codes are stored under. Never committed, never logged.</summary>
    public string CodeSecret { get; set; } = string.Empty;

    /// <summary>At most this many codes per email address in any one minute.</summary>
    public int CodeRequestsPerAddressPerMinute { get; set; } = 1;

    /// <summary>At most this many codes per email address in any one hour.</summary>
    public int CodeRequestsPerAddressPerHour { get; set; } = 5;

    /// <summary>The issuer without a trailing slash, the form the discovery document and the keys URL are built from.</summary>
    public string IssuerBase => Issuer.TrimEnd('/');
}
