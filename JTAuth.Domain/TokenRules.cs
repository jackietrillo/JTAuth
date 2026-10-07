namespace JTAuth.Domain;

public static class TokenRules
{
    /// <summary>Access tokens are short-lived; apps validate them locally and renew them with a refresh token.</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
}
