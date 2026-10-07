using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.Domain;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("pat@example.com", "pat@example.com")]
    [InlineData("  Pat@Example.COM  ", "pat@example.com")]
    [InlineData("PAT@EXAMPLE.COM", "pat@example.com")]
    [InlineData("\tpat.smith+tag@Mail.Example.org\n", "pat.smith+tag@mail.example.org")]
    public void AddressesAreTrimmedAndLowerCased(string typed, string expected)
    {
        EmailAddress.Normalize(typed).ShouldBe(expected);
        EmailAddress.TryNormalize(typed, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Fact]
    public void TwoSpellingsOfOneAddressNormalizeToTheSameValue()
    {
        EmailAddress.Normalize("Pat@Example.com").ShouldBe(EmailAddress.Normalize(" pat@EXAMPLE.com "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pat")]
    [InlineData("pat@")]
    [InlineData("@example.com")]
    [InlineData("pat@example")]
    [InlineData("pat@@example.com")]
    [InlineData("pat@exa@mple.com")]
    [InlineData("pat smith@example.com")]
    [InlineData("pat@example..com")]
    [InlineData("pat@.example.com")]
    [InlineData("pat@example.com.")]
    [InlineData("Pat <pat@example.com>")]
    [InlineData("pat@example.com, other@example.com")]
    [InlineData("pat@example.com;other@example.com")]
    [InlineData("\"pat\"@example.com")]
    [InlineData("pat@-example.com")]
    public void AddressesThatAreNotOneMailboxAreRejected(string? typed)
    {
        EmailAddress.TryNormalize(typed, out var normalized).ShouldBeFalse();
        normalized.ShouldBeEmpty();
    }

    [Fact]
    public void OverlongAddressesAreRejected()
    {
        EmailAddress.TryNormalize(new string('a', 65) + "@example.com", out _).ShouldBeFalse();
        EmailAddress.TryNormalize("a@" + new string('b', 250) + ".com", out _).ShouldBeFalse();
    }
}
