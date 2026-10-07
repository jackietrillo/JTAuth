namespace JTAuth.Domain;

/// <summary>A person. Holds no email, phone or password: those are identities, and what the person may do is up to each app.</summary>
public sealed record User(Guid Id, string? DisplayName, DateTimeOffset CreatedUtc);
