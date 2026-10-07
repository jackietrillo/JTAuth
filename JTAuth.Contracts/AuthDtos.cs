using System.Text.Json.Serialization;

namespace JTAuth.Contracts;

// JTAuth's API surface. Client apps (CityBars, ...) define their own copies of what they read.

/// <summary>Starts a sign-in: JTAuth emails a one-time code to the address.</summary>
public sealed record RequestCodeRequest(string ClientId, string Email);

/// <summary>The answer to a code request. It is the same for every address, whether or not it has an account.</summary>
public sealed record CodeRequestedDto(int ExpiresInSeconds);

/// <summary>Finishes a sign-in with the code from the email. Signing up and signing in are the same step.</summary>
public sealed record VerifyCodeRequest(string ClientId, string Email, string Code);

/// <summary>What a successful sign-in returns. <see cref="IsNewUser"/> is true when this sign-in created the account.</summary>
public sealed record AuthTokensDto(string AccessToken, DateTimeOffset AccessTokenExpiresUtc, bool IsNewUser);

public sealed record ProfileDto;

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
