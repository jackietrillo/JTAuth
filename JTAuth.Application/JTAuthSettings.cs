namespace JTAuth.Application;

/// <summary>Configuration read from the <c>JTAuth</c> section.</summary>
public sealed class JTAuthSettings
{
    public const string SectionName = "JTAuth";

    /// <summary>The API's public base URL: the token <c>iss</c>, and where the published keys are found.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The server secret that keys the hash codes are stored under. Never committed, never logged.</summary>
    public string CodeSecret { get; set; } = string.Empty;

    /// <summary>
    /// The Google OAuth client ids whose ID tokens are accepted (a Google token names the client it was issued for). Several are
    /// normal: one per environment and one per platform. Empty means Google sign-in is not offered.
    /// </summary>
    public string[] GoogleClientIds { get; set; } = [];

    /// <summary>The configured Google client ids without blank entries, which would otherwise match a token that names no audience.</summary>
    public IReadOnlyList<string> UsableGoogleClientIds => GoogleClientIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();

    /// <summary>
    /// While there is no email service: the code is returned in the answer to the code request, for the page to fill in, and no email
    /// is sent. Anyone who types an address can then sign in as it, so this is for testing and must be off once email works.
    /// </summary>
    public bool ShowCodeWithoutEmail { get; set; }

    /// <summary>At most this many codes per email address in any one minute.</summary>
    public int CodeRequestsPerAddressPerMinute { get; set; } = 1;

    /// <summary>At most this many codes per email address in any one hour.</summary>
    public int CodeRequestsPerAddressPerHour { get; set; } = 5;

    /// <summary>The issuer without a trailing slash, the form the discovery document and the keys URL are built from.</summary>
    public string IssuerBase => Issuer.TrimEnd('/');
}
