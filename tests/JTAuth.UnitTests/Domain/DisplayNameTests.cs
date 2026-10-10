using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.Domain;

public sealed class DisplayNameTests
{
    [Theory]
    [InlineData("Pat", "Pat")]
    [InlineData("  Pat Smith  ", "Pat Smith")]
    [InlineData("José Ñandú", "José Ñandú")]
    [InlineData("A", "A")]
    public void AGoodNameIsTrimmed(string typed, string expected)
    {
        DisplayName.TryNormalize(typed, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Fact]
    public void ANameCanBeFiftyCharactersButNotFiftyOne()
    {
        DisplayName.TryNormalize(new string('a', 50), out _).ShouldBeTrue();
        DisplayName.TryNormalize(new string('a', 51), out _).ShouldBeFalse();
        DisplayName.TryNormalize("  " + new string('a', 50) + "  ", out _).ShouldBeTrue("the limit is on the trimmed name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pat\nSmith")]
    [InlineData("Pat\tSmith")]
    [InlineData("Pat\u0000")]
    public void ABlankNameOrOneWithControlCharactersIsRejected(string? typed)
    {
        DisplayName.TryNormalize(typed, out var normalized).ShouldBeFalse();
        normalized.ShouldBeEmpty();
    }
}
