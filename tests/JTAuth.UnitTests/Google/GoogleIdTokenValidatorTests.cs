using System.Security.Cryptography;
using JTAuth.Application;
using JTAuth.Infrastructure.Google;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace JTAuth.UnitTests.Google;

/// <summary>The checks on a Google ID token, against a key pair made here standing in for Google's published keys.</summary>
public sealed class GoogleIdTokenValidatorTests : IDisposable
{
    private const string ClientId = "492151695513-test.apps.googleusercontent.com";

    private readonly RSA googleKey = RSA.Create(2048);
    private readonly RSA otherKey = RSA.Create(2048);

    public void Dispose()
    {
        googleKey.Dispose();
        otherKey.Dispose();
    }

    private GoogleIdTokenValidator NewValidator(params string[] clientIds)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = "https://accounts.google.com" };
        configuration.SigningKeys.Add(new RsaSecurityKey(googleKey.ExportParameters(includePrivateParameters: false)) { KeyId = "google-key-1" });
        return new GoogleIdTokenValidator(
            new JTAuthSettings { GoogleClientIds = clientIds.Length == 0 ? [ClientId] : clientIds },
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }

    private string Token(
        string issuer = "https://accounts.google.com",
        string audience = ClientId,
        Dictionary<string, object>? claims = null,
        TimeSpan? expiresIn = null,
        RSA? signedWith = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var all = new Dictionary<string, object>
        {
            ["sub"] = "110169484474386276334",
            ["email"] = "Pat@Example.com",
            ["email_verified"] = true,
            ["name"] = "Pat Smith",
        };
        foreach (var (name, value) in claims ?? [])
        {
            all[name] = value;
        }

        var now = DateTime.UtcNow;
        var key = new RsaSecurityKey(signedWith ?? googleKey) { KeyId = "google-key-1" };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = all,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now + (expiresIn ?? TimeSpan.FromMinutes(50)),
            SigningCredentials = new SigningCredentials(key, algorithm),
        });
    }

    [Fact]
    public async Task AGenuineTokenForThisServicesGoogleClientGivesTheAccountAndTheVerifiedEmail()
    {
        var profile = await NewValidator().ValidateAsync(Token(), TestContext.Current.CancellationToken);

        profile.ShouldBe(new GoogleProfile("110169484474386276334", "Pat@Example.com", "Pat Smith"));
    }

    [Fact]
    public async Task BothFormsOfGooglesIssuerAreAccepted()
    {
        var validator = NewValidator();

        (await validator.ValidateAsync(Token(issuer: "https://accounts.google.com"), TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await validator.ValidateAsync(Token(issuer: "accounts.google.com"), TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AnyOfSeveralConfiguredClientIdsIsAccepted()
    {
        var validator = NewValidator("web-test-client", "web-prod-client", "ios-client");

        (await validator.ValidateAsync(Token(audience: "web-prod-client"), TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await validator.ValidateAsync(Token(audience: "ios-client"), TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await validator.ValidateAsync(Token(audience: "someone-elses-client"), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATokenIssuedToAnotherAppsGoogleClientIsRefused()
    {
        (await NewValidator().ValidateAsync(Token(audience: "another-app.apps.googleusercontent.com"), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATokenFromAnotherIssuerIsRefused()
    {
        (await NewValidator().ValidateAsync(Token(issuer: "https://evil.example.com"), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKeyIsRefused()
    {
        (await NewValidator().ValidateAsync(Token(signedWith: otherKey), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        (await NewValidator().ValidateAsync(Token(expiresIn: TimeSpan.FromMinutes(-10)), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATokenWhoseEmailGoogleHasNotVerifiedIsRefused()
    {
        var unverified = Token(claims: new Dictionary<string, object> { ["email_verified"] = false });

        (await NewValidator().ValidateAsync(unverified, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AVerifiedFlagSentAsTextIsReadToo()
    {
        var asText = Token(claims: new Dictionary<string, object> { ["email_verified"] = "true" });
        var asFalseText = Token(claims: new Dictionary<string, object> { ["email_verified"] = "false" });

        (await NewValidator().ValidateAsync(asText, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await NewValidator().ValidateAsync(asFalseText, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATokenWithNoEmailOrNoSubjectIsRefused()
    {
        var noEmail = Token(claims: new Dictionary<string, object> { ["email"] = "" });
        var noSubject = Token(claims: new Dictionary<string, object> { ["sub"] = "" });

        (await NewValidator().ValidateAsync(noEmail, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await NewValidator().ValidateAsync(noSubject, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATokenWithoutANameStillGivesTheAccount()
    {
        var token = Token(claims: new Dictionary<string, object> { ["name"] = "" });

        var profile = await NewValidator().ValidateAsync(token, TestContext.Current.CancellationToken);

        profile!.Subject.ShouldBe("110169484474386276334");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task SomethingThatIsNotAJwtIsRefused(string token)
    {
        (await NewValidator().ValidateAsync(token, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATokenSignedWithAnUnexpectedAlgorithmIsRefused()
    {
        var hmac = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://accounts.google.com",
            Audience = ClientId,
            Claims = new Dictionary<string, object> { ["sub"] = "1", ["email"] = "a@example.com", ["email_verified"] = true },
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(new byte[64]), SecurityAlgorithms.HmacSha256),
        });

        (await NewValidator().ValidateAsync(hmac, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task BlankClientIdsAreIgnoredSoTheyCannotMatchATokenWithNoAudience()
    {
        var validator = NewValidator("", "  ");

        (await validator.ValidateAsync(Token(), TestContext.Current.CancellationToken)).ShouldBeNull();
        new JTAuthSettings { GoogleClientIds = ["", "  ", " real-id "] }.UsableGoogleClientIds.ShouldBe(["real-id"]);
    }

    [Fact]
    public async Task WithNoGoogleClientIdConfiguredNothingIsAccepted()
    {
        var validator = new GoogleIdTokenValidator(new JTAuthSettings(), new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration()));

        (await validator.ValidateAsync(Token(), TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
