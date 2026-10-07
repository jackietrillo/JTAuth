namespace JTAuth.Domain;

/// <summary>An app registered to sign users in. Its <see cref="Audience"/> becomes the <c>aud</c> claim of its tokens.</summary>
/// <param name="Id">The database key, referenced by the rows that belong to the app.</param>
/// <param name="ClientId">What the app sends, for example <c>citybars</c>.</param>
/// <param name="Name">Shown to people, for example in "Your CityBars code is 123456".</param>
public sealed record Client(int Id, string ClientId, string Name, string Audience, bool IsEnabled);
