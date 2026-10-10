using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.Domain;

public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.NewGuid();

    private static RefreshToken NewChain() => RefreshToken.StartChain(UserId, 1, RefreshTokenRules.Generate().Hash, Now);

    [Fact]
    public void ARefreshTokenLastsThirtyDays() => RefreshTokenRules.Lifetime.ShouldBe(TimeSpan.FromDays(30));

    [Fact]
    public void GeneratedTokensAreLongRandomUrlSafeAndDifferentEachTime()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => RefreshTokenRules.Generate().Token).ToList();

        tokens.Distinct().Count().ShouldBe(500);
        tokens.ShouldAllBe(token => token.Length == 43 && token.All(character => char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_'));
    }

    [Fact]
    public void WhatIsStoredIsAHashOfTheTokenNotTheToken()
    {
        var (token, hash) = RefreshTokenRules.Generate();

        hash.Length.ShouldBe(32);
        hash.ShouldBe(RefreshTokenRules.Hash(token));
        RefreshTokenRules.Hash(token + "x").ShouldNotBe(hash);
        Convert.ToHexString(hash).ShouldNotContain(token);
    }

    [Fact]
    public void AChainStartsWithAFreshTokenThatExpiresThirtyDaysLater()
    {
        var first = NewChain();

        first.ExpiresUtc.ShouldBe(Now.AddDays(30));
        first.RevokedUtc.ShouldBeNull();
        first.UserId.ShouldBe(UserId);
        first.ClientId.ShouldBe(1);
        first.FamilyId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void EachSignInStartsItsOwnChain() => NewChain().FamilyId.ShouldNotBe(NewChain().FamilyId);

    [Fact]
    public void TheNextTokenStaysInTheChainAndGetsAFullNewLifetime()
    {
        var first = NewChain();
        var later = Now.AddDays(10);

        var next = first.Next(RefreshTokenRules.Generate().Hash, later);

        next.FamilyId.ShouldBe(first.FamilyId);
        next.UserId.ShouldBe(first.UserId);
        next.ClientId.ShouldBe(first.ClientId);
        next.ExpiresUtc.ShouldBe(later.AddDays(30));
        next.TokenHash.ShouldNotBe(first.TokenHash);
        next.RevokedUtc.ShouldBeNull();
    }

    [Fact]
    public void ATokenExpiresAfterThirtyDaysAndNotBefore()
    {
        var token = NewChain();

        token.StateAt(Now).ShouldBe(RefreshTokenState.Usable);
        token.StateAt(Now.AddDays(30).AddTicks(-1)).ShouldBe(RefreshTokenState.Usable);
        token.StateAt(Now.AddDays(30)).ShouldBe(RefreshTokenState.Expired);
    }

    [Fact]
    public void ARevokedTokenIsRevokedEvenAfterItHasAlsoExpired()
    {
        var revoked = NewChain() with { RevokedUtc = Now.AddMinutes(1) };

        revoked.StateAt(Now.AddMinutes(2)).ShouldBe(RefreshTokenState.Revoked);
        revoked.StateAt(Now.AddDays(60)).ShouldBe(RefreshTokenState.Revoked);
    }
}
