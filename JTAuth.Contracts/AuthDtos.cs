using System.Text.Json.Serialization;

namespace JTAuth.Contracts;

// JTAuth's API surface. Client apps (CityBars, ...) define their own copies of what they read.

/// <summary>Starts a sign-in: JTAuth emails a one-time code to the address.</summary>
public sealed record RequestCodeRequest(string ClientId, string Email);

/// <summary>The answer to a code request. It is the same for every address, whether or not it has an account.</summary>
public sealed record CodeRequestedDto(int ExpiresInSeconds);

/// <summary>Finishes a sign-in with the code from the email. Signing up and signing in are the same step.</summary>
public sealed record VerifyCodeRequest(string ClientId, string Email, string Code);

/// <summary>
/// What a successful sign-in or refresh returns: a short-lived access token and the refresh token that renews it.
/// <see cref="IsNewUser"/> is true only when this sign-in created the account.
/// </summary>
public sealed record AuthTokensDto(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresUtc,
    bool IsNewUser);

/// <summary>Exchanges a refresh token for a new access token and a new refresh token. The old refresh token stops working.</summary>
public sealed record RefreshRequest(string ClientId, string RefreshToken);

/// <summary>Ends the session the refresh token belongs to.</summary>
public sealed record LogoutRequest(string RefreshToken);

/// <summary>The person's own details: their display name and how they can sign in.</summary>
public sealed record ProfileDto(string? DisplayName, IReadOnlyList<SignInMethodDto> SignInMethods);

/// <summary>One way of signing in. <see cref="Status"/> is <c>Linked</c>, <c>NotLinked</c> or <c>ComingSoon</c>; <see cref="Address"/> is the email address for a linked email method.</summary>
public sealed record SignInMethodDto(string Provider, string Status, string? Address);

public sealed record UpdateProfileRequest(string DisplayName);

/// <summary>Starts changing the sign-in email: a code is sent to the new address.</summary>
public sealed record RequestEmailChangeRequest(string NewEmail);

/// <summary>Finishes changing the sign-in email with the code sent to the new address.</summary>
public sealed record ConfirmEmailChangeRequest(string NewEmail, string Code);

/// <summary>The public signing keys (a JSON Web Key Set), so apps can check tokens without a shared secret.</summary>
public sealed record JwksDto([property: JsonPropertyName("keys")] IReadOnlyList<JsonWebKeyDto> Keys);

public sealed record JsonWebKeyDto(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("alg")] string Algorithm,
    [property: JsonPropertyName("kid")] string KeyId,
    [property: JsonPropertyName("n")] string Modulus,
    [property: JsonPropertyName("e")] string Exponent);

/// <summary>The discovery document stock JWT middleware reads when given the issuer as its authority.</summary>
public sealed record OpenIdConfigurationDto(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> SigningAlgorithms);
