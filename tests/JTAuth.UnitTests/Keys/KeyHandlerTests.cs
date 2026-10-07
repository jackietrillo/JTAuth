using JTAuth.Application;
using JTAuth.Application.Keys;
using NSubstitute;
using Shouldly;

namespace JTAuth.UnitTests.Keys;

public sealed class KeyHandlerTests
{
    private static ISigningKeySource Keys(params PublicSigningKey[] keys)
    {
        var source = Substitute.For<ISigningKeySource>();
        source.PublishedKeys.Returns(keys);
        return source;
    }

    [Fact]
    public async Task JwksPublishesEveryKeyWithItsKeyId()
    {
        var handler = new GetJwksHandler(Keys(new PublicSigningKey("kid-new", "RS256", "AQAB-n1", "AQAB"), new PublicSigningKey("kid-old", "RS256", "AQAB-n2", "AQAB")));

        var result = await handler.HandleAsync(new GetJwksQuery(), CancellationToken.None);

        result.Value!.Keys.Select(key => key.KeyId).ShouldBe(["kid-new", "kid-old"]);
        result.Value.Keys.ShouldAllBe(key => key.KeyType == "RSA" && key.Use == "sig" && key.Algorithm == "RS256");
        result.Value.Keys[0].Modulus.ShouldBe("AQAB-n1");
    }

    [Fact]
    public async Task DiscoveryNamesTheIssuerAndWhereItsKeysAre()
    {
        var settings = new JTAuthSettings { Issuer = "https://auth.example.com/" };
        var handler = new GetOpenIdConfigurationHandler(settings, Keys(new PublicSigningKey("kid", "RS256", "n", "AQAB")));

        var result = await handler.HandleAsync(new GetOpenIdConfigurationQuery(), CancellationToken.None);

        result.Value!.Issuer.ShouldBe("https://auth.example.com");
        result.Value.JwksUri.ShouldBe("https://auth.example.com/.well-known/jwks.json");
        result.Value.SigningAlgorithms.ShouldBe(["RS256"]);
    }
}
