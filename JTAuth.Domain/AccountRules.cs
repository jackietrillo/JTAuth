namespace JTAuth.Domain;

/// <summary>Rules about what a person may do to their own account.</summary>
public static class AccountRules
{
    /// <summary>How recently the person must have proved who they are (a code or Google) to do something that cannot be undone.</summary>
    public static readonly TimeSpan RecentSignInWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether the sign-in at <paramref name="signedInUtc"/> counts as recent at <paramref name="now"/>. A token with no sign-in time
    /// (issued before the time was kept) never does, and neither does one that claims to be from the future.
    /// </summary>
    public static bool IsRecentSignIn(DateTimeOffset? signedInUtc, DateTimeOffset now) =>
        signedInUtc is { } at && at <= now.AddMinutes(1) && now - at <= RecentSignInWindow;
}
