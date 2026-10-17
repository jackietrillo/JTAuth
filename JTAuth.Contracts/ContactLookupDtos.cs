namespace JTAuth.Contracts;

/// <summary>Asks for the sign-in email of some of an app's own users, by their user id (the token's <c>sub</c>). At most 200 at a time.</summary>
public sealed record ContactLookupRequest(IReadOnlyList<Guid> UserIds);

/// <summary>One person of the calling app. <see cref="Email"/> is null when the account has no email address.</summary>
public sealed record ContactLookupItemDto(Guid UserId, string? Email);

/// <summary>The people from the request who have signed in to the calling app. Anyone else is left out, whoever they are.</summary>
public sealed record ContactLookupDto(IReadOnlyList<ContactLookupItemDto> Items);