using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.Domain;

public sealed class LoginCodeRulesTests
{
    [Fact]
    public void TheRulesAreSixDigitsTenMinutesFiveAttempts()
    {
        LoginCodeRules.CodeLength.ShouldBe(6);
        LoginCodeRules.Lifetime.ShouldBe(TimeSpan.FromMinutes(10));
        LoginCodeRules.MaxAttempts.ShouldBe(5);
    }

    [Fact]
    public void GeneratedCodesAreSixDigitsIncludingLeadingZeros()
    {
        var codes = Enumerable.Range(0, 2_000).Select(_ => LoginCodeRules.GenerateCode()).ToList();

        codes.ShouldAllBe(code => LoginCodeRules.IsWellFormed(code));
        codes.Distinct().Count().ShouldBeGreaterThan(1_500, "codes must be random, not a fixed or tiny set");
        codes.ShouldContain(code => code.StartsWith('0'), "a leading zero is a legitimate code and must not be dropped");
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData("12 456", false)]
    [InlineData("１２３４５６", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlySixAsciiDigitsAreWellFormed(string? code, bool expected) =>
        LoginCodeRules.IsWellFormed(code).ShouldBe(expected);

    [Fact]
    public void ACodeIsStoredAsAHashThatIsNotTheCode()
    {
        var hash = LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", "123456");

        hash.Length.ShouldBe(32);
        hash.ShouldNotBe(System.Text.Encoding.UTF8.GetBytes("123456"));
        Convert.ToHexString(hash).ShouldNotContain("123456");
    }

    [Fact]
    public void TheSameInputsAlwaysGiveTheSameHash()
    {
        LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", "123456")
            .ShouldBe(LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", "123456"));
    }

    [Fact]
    public void AHashFitsOnlyItsOwnCodeAddressAndSecret()
    {
        var hash = LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", "123456");

        LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", "123457").ShouldNotBe(hash);
        LoginCodeRules.Hash("secret", IdentityProvider.Email, "sam@example.com", "123456").ShouldNotBe(hash);
        LoginCodeRules.Hash("secret", IdentityProvider.Phone, "pat@example.com", "123456").ShouldNotBe(hash);
        LoginCodeRules.Hash("other-secret", IdentityProvider.Email, "pat@example.com", "123456").ShouldNotBe(hash);
    }
}
