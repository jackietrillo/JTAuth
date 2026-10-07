using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.Domain;

public sealed class LoginCodeTests
{
    private static readonly DateTimeOffset Issued = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static LoginCode NewCode(string code = "123456") =>
        LoginCode.Issue(IdentityProvider.Email, "pat@example.com", LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", code), Issued);

    [Fact]
    public void ACodeExpiresTenMinutesAfterItIsIssued()
    {
        var code = NewCode();

        code.ExpiresUtc.ShouldBe(Issued.AddMinutes(10));
        code.StateAt(Issued).ShouldBe(LoginCodeState.Usable);
        code.StateAt(Issued.AddMinutes(10).AddTicks(-1)).ShouldBe(LoginCodeState.Usable);
        code.StateAt(Issued.AddMinutes(10)).ShouldBe(LoginCodeState.Expired);
        code.StateAt(Issued.AddHours(2)).ShouldBe(LoginCodeState.Expired);
    }

    [Fact]
    public void ACodeThatWasUsedCannotBeUsedAgain()
    {
        var used = NewCode() with { ConsumedUtc = Issued.AddMinutes(1) };

        used.StateAt(Issued.AddMinutes(2)).ShouldBe(LoginCodeState.AlreadyUsed);
    }

    [Theory]
    [InlineData(0, LoginCodeState.Usable)]
    [InlineData(4, LoginCodeState.Usable)]
    [InlineData(5, LoginCodeState.AttemptsExhausted)]
    [InlineData(6, LoginCodeState.AttemptsExhausted)]
    public void ACodeIsDeadAfterFiveAttempts(int attempts, LoginCodeState expected)
    {
        (NewCode() with { AttemptCount = attempts }).StateAt(Issued.AddMinutes(1)).ShouldBe(expected);
    }

    [Fact]
    public void ANewCodeStartsWithNoAttemptsAndNotUsed()
    {
        var code = NewCode();

        code.AttemptCount.ShouldBe(0);
        code.ConsumedUtc.ShouldBeNull();
        code.CreatedUtc.ShouldBe(Issued);
    }

    [Fact]
    public void ACodeMatchesOnlyItsOwnHash()
    {
        var code = NewCode("123456");

        code.Matches(LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", "123456")).ShouldBeTrue();
        code.Matches(LoginCodeRules.Hash("secret", IdentityProvider.Email, "pat@example.com", "654321")).ShouldBeFalse();
    }
}
