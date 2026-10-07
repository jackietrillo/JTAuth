using JTAuth.Application;
using JTAuth.Domain;
using JTAuth.Infrastructure.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace JTAuth.UnitTests.Tokens;

public sealed class TokenTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly Client CityBars = new(1, "citybars", "CityBars", "citybars", true);

    private readonly string folder = Path.Combine(Path.GetTempPath(), "jtauth-tests-" + Guid.NewGuid().ToString("N"));

    private string KeyFile => Path.Combine(folder, "keys", "signing-key.json");

    private static AccessTokenIssuer NewIssuer(RsaSigningKeys keys) => new(keys, new JTAuthSettings { Issuer = "https://auth.example.com/" });

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static TokenValidationParameters Validation(RsaSigningKeys keys, string audience, DateTimeOffset? at = null)
    {
        var published = keys.PublishedKeys.Single();
        return new TokenValidationParameters
        {
            ValidIssuer = "https://auth.example.com",
            ValidAudience = audience,
            IssuerSigningKey = new JsonWebKey
            {
                Kty = "RSA",
                Kid = published.KeyId,
                N = published.Modulus,
                E = published.Exponent,
            },
            LifetimeValidator = (notBefore, expires, _, _) =>
                (at ?? Now) >= new DateTimeOffset(notBefore!.Value) && (at ?? Now) < new DateTimeOffset(expires!.Value),
        };
    }

    [Fact]
    public async Task TheTokenCarriesTheStandardClaimsForTheClient()
    {
        var keys = RsaSigningKeys.LoadOrCreate(KeyFile);
        var user = new User(Guid.NewGuid(), "Pat", Now);

        var issued = NewIssuer(keys).Issue(user, CityBars, Now);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(issued.Token, Validation(keys, "citybars"));

        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        var token = (JsonWebToken)result.SecurityToken;
        token.Issuer.ShouldBe("https://auth.example.com");
        token.Audiences.ShouldBe(["citybars"]);
        token.Subject.ShouldBe(user.Id.ToString());
        token.GetClaim("name").Value.ShouldBe("Pat");
        token.IssuedAt.ShouldBe(Now.UtcDateTime);
        token.ValidTo.ShouldBe(Now.AddMinutes(15).UtcDateTime);
        token.Id.ShouldNotBeNullOrWhiteSpace();
        token.Alg.ShouldBe("RS256");
        token.Kid.ShouldBe(keys.PublishedKeys.Single().KeyId);
        issued.ExpiresUtc.ShouldBe(Now.AddMinutes(15));
    }

    [Fact]
    public void ThereIsNoNameClaimUntilTheUserHasADisplayName()
    {
        var keys = RsaSigningKeys.LoadOrCreate(KeyFile);

        var token = new JsonWebToken(NewIssuer(keys).Issue(new User(Guid.NewGuid(), null, Now), CityBars, Now).Token);

        token.TryGetClaim("name", out _).ShouldBeFalse();
    }

    [Fact]
    public void EveryTokenHasItsOwnId()
    {
        var keys = RsaSigningKeys.LoadOrCreate(KeyFile);
        var issuer = NewIssuer(keys);
        var user = new User(Guid.NewGuid(), null, Now);

        new JsonWebToken(issuer.Issue(user, CityBars, Now).Token).Id
            .ShouldNotBe(new JsonWebToken(issuer.Issue(user, CityBars, Now).Token).Id);
    }

    [Fact]
    public async Task ATokenForOneAppIsRejectedWhenCheckedForAnotherAudience()
    {
        var keys = RsaSigningKeys.LoadOrCreate(KeyFile);
        var token = NewIssuer(keys).Issue(new User(Guid.NewGuid(), "Pat", Now), CityBars, Now).Token;

        (await new JsonWebTokenHandler().ValidateTokenAsync(token, Validation(keys, "citybars"))).IsValid.ShouldBeTrue();
        (await new JsonWebTokenHandler().ValidateTokenAsync(token, Validation(keys, "menugenerator"))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ATokenIsValidForFifteenMinutesOnly()
    {
        var keys = RsaSigningKeys.LoadOrCreate(KeyFile);
        var token = NewIssuer(keys).Issue(new User(Guid.NewGuid(), "Pat", Now), CityBars, Now).Token;
        var handler = new JsonWebTokenHandler();

        (await handler.ValidateTokenAsync(token, Validation(keys, "citybars", Now.AddMinutes(14).AddSeconds(59)))).IsValid.ShouldBeTrue();
        (await handler.ValidateTokenAsync(token, Validation(keys, "citybars", Now.AddMinutes(15)))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ATokenSignedByAnotherKeyIsRejected()
    {
        var keys = RsaSigningKeys.LoadOrCreate(KeyFile);
        var impostor = RsaSigningKeys.LoadOrCreate(Path.Combine(folder, "other", "signing-key.json"));
        var forged = NewIssuer(impostor).Issue(new User(Guid.NewGuid(), "Pat", Now), CityBars, Now).Token;

        (await new JsonWebTokenHandler().ValidateTokenAsync(forged, Validation(keys, "citybars"))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void TheKeyFileIsCreatedOnFirstUseAndReusedAfterwards()
    {
        File.Exists(KeyFile).ShouldBeFalse();

        var first = RsaSigningKeys.LoadOrCreate(KeyFile);
        File.Exists(KeyFile).ShouldBeTrue();
        var second = RsaSigningKeys.LoadOrCreate(KeyFile);

        second.PublishedKeys.Single().ShouldBe(first.PublishedKeys.Single());
    }

    [Fact]
    public void DifferentKeyFilesGiveDifferentKeyIds()
    {
        var one = RsaSigningKeys.LoadOrCreate(KeyFile);
        var other = RsaSigningKeys.LoadOrCreate(Path.Combine(folder, "other", "signing-key.json"));

        one.PublishedKeys.Single().KeyId.ShouldNotBe(other.PublishedKeys.Single().KeyId);
    }
}
