using JTAuth.Application;
using JTAuth.Application.SignIn;
using JTAuth.Application.Tokens;
using JTAuth.BuildingBlocks;
using JTAuth.Domain;
using JTAuth.UnitTests.SignIn;
using NSubstitute;
using Shouldly;

namespace JTAuth.UnitTests.Tokens;

public sealed class RefreshTokensHandlerTests
{
    private static void ShouldBeRefusedAsInvalid<T>(Result<T> result)
    {
        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        result.Error.Code.ShouldBe(RefreshTokensHandler.InvalidRefreshTokenCode);
    }

    [Fact]
    public async Task ASignInReturnsARefreshTokenThatLastsThirtyDaysAndStoresOnlyItsHash()
    {
        var world = new SignInWorld();
        var signedInAt = world.Clock.GetUtcNow();

        var tokens = await world.SignInAsync();

        tokens.RefreshToken.Length.ShouldBe(43);
        tokens.RefreshTokenExpiresUtc.ShouldBe(signedInAt.AddDays(30));
        var stored = world.RefreshTokens.All.Single();
        stored.TokenHash.ShouldBe(RefreshTokenRules.Hash(tokens.RefreshToken));
        stored.ClientId.ShouldBe(1);
        stored.UserId.ShouldBe(world.UserIdOf());
    }

    [Fact]
    public async Task RefreshingReturnsANewPairAndRevokesTheOldRefreshToken()
    {
        var world = new SignInWorld();
        var first = await world.SignInAsync();

        var result = await world.RefreshAsync(first.RefreshToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.Value.RefreshToken.ShouldNotBe(first.RefreshToken);
        result.Value.IsNewUser.ShouldBeFalse();
        result.Value.AccessTokenExpiresUtc.ShouldBe(world.Clock.GetUtcNow().AddMinutes(15));
        world.RefreshTokens.All.Count(token => token.RevokedUtc is null).ShouldBe(1, "only the new token is live");
        world.RefreshTokens.All.Single(token => token.RevokedUtc is not null).TokenHash.ShouldBe(RefreshTokenRules.Hash(first.RefreshToken));
    }

    [Fact]
    public async Task EachRefreshGivesATokenThatCanBeRefreshedAgain()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();

        for (var round = 0; round < 3; round++)
        {
            world.Clock.Advance(TimeSpan.FromMinutes(15));
            var result = await world.RefreshAsync(tokens.RefreshToken);
            result.IsSuccess.ShouldBeTrue($"refresh {round + 1}");
            tokens = result.Value!;
        }

        world.RefreshTokens.All.Select(token => token.FamilyId).Distinct().Count().ShouldBe(1, "one chain");
        world.RefreshTokens.All.Count.ShouldBe(4);
    }

    [Fact]
    public async Task EachRefreshGivesAFullNewThirtyDays()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();
        world.Clock.Advance(TimeSpan.FromDays(20));
        var refreshedAt = world.Clock.GetUtcNow();

        var result = await world.RefreshAsync(tokens.RefreshToken);

        result.Value!.RefreshTokenExpiresUtc.ShouldBe(refreshedAt.AddDays(30));
    }

    [Fact]
    public async Task ReusingATokenThatWasAlreadyRefreshedEndsTheWholeChain()
    {
        var world = new SignInWorld();
        var first = await world.SignInAsync();
        var second = (await world.RefreshAsync(first.RefreshToken)).Value!;
        var third = (await world.RefreshAsync(second.RefreshToken)).Value!;

        ShouldBeRefusedAsInvalid(await world.RefreshAsync(first.RefreshToken));

        world.RefreshTokens.All.ShouldAllBe(token => token.RevokedUtc != null);
        ShouldBeRefusedAsInvalid(await world.RefreshAsync(third.RefreshToken));
        ShouldBeRefusedAsInvalid(await world.RefreshAsync(second.RefreshToken));
    }

    [Fact]
    public async Task ReuseEndsOnlyThatSignInsChainNotTheSamePersonsOtherSignIns()
    {
        var world = new SignInWorld();
        var phone = await world.SignInAsync();
        var laptop = await world.SignInAsync();
        var phoneNext = (await world.RefreshAsync(phone.RefreshToken)).Value!;

        ShouldBeRefusedAsInvalid(await world.RefreshAsync(phone.RefreshToken));

        ShouldBeRefusedAsInvalid(await world.RefreshAsync(phoneNext.RefreshToken));
        (await world.RefreshAsync(laptop.RefreshToken)).IsSuccess.ShouldBeTrue("the laptop's chain is untouched");
    }

    [Fact]
    public async Task ATokenIsRefusedOnceThirtyDaysHavePassed()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();

        world.Clock.Advance(TimeSpan.FromDays(30));

        ShouldBeRefusedAsInvalid(await world.RefreshAsync(tokens.RefreshToken));
    }

    [Fact]
    public async Task ATokenStillWorksJustBeforeThirtyDays()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();

        world.Clock.Advance(TimeSpan.FromDays(30).Subtract(TimeSpan.FromMinutes(5)));

        (await world.RefreshAsync(tokens.RefreshToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnknownTokenGetsTheSameAnswerAsAReusedOne()
    {
        var world = new SignInWorld();
        var first = await world.SignInAsync();
        await world.RefreshAsync(first.RefreshToken);

        var unknown = await world.RefreshAsync(RefreshTokenRules.Generate().Token);
        var reused = await world.RefreshAsync(first.RefreshToken);

        ShouldBeRefusedAsInvalid(unknown);
        ShouldBeRefusedAsInvalid(reused);
        unknown.Error.ShouldBe(reused.Error);
    }

    [Fact]
    public async Task ATokenPresentedToAnotherAppIsRefusedAndDoesNotEndItsChain()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync(clientId: "citybars");

        ShouldBeRefusedAsInvalid(await world.RefreshAsync(tokens.RefreshToken, "otherapp"));

        (await world.RefreshAsync(tokens.RefreshToken, "citybars")).IsSuccess.ShouldBeTrue("a mix-up between apps is not a leak");
    }

    [Fact]
    public async Task AnUnknownClientIsRefused()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();

        var result = await world.RefreshAsync(tokens.RefreshToken, "nosuchapp");

        result.Error!.Type.ShouldBe(ResultErrorType.Validation);
        result.Error.Code.ShouldBe(ClientCheck.UnknownClientCode);
    }

    [Fact]
    public async Task ADisabledClientIsRefusedAndNothingIsRotated()
    {
        var world = new SignInWorld();
        var (token, hash) = RefreshTokenRules.Generate();
        var userId = (await world.Users.GetOrCreateByIdentityAsync(IdentityProvider.Email, "pat@example.com", world.Clock.GetUtcNow(), CancellationToken.None)).User.Id;
        await world.RefreshTokens.AddAsync(RefreshToken.StartChain(userId, 3, hash, world.Clock.GetUtcNow()), CancellationToken.None);

        var result = await world.RefreshAsync(token, "retired");

        result.Error!.Type.ShouldBe(ResultErrorType.Forbidden);
        result.Error.Code.ShouldBe(ClientCheck.DisabledClientCode);
        world.RefreshTokens.All.Single().RevokedUtc.ShouldBeNull();
    }

    [Fact]
    public async Task ARefreshIsNotASignInSoTheLastSignInTimeDoesNotMove()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();

        await world.RefreshAsync(tokens.RefreshToken);

        world.UserClients.SignIns.Count.ShouldBe(1);
    }

    [Fact]
    public async Task APersonWhoNoLongerExistsCannotRefresh()
    {
        var world = new SignInWorld();
        var orphan = RefreshTokenRules.Generate();
        await world.RefreshTokens.AddAsync(RefreshToken.StartChain(Guid.NewGuid(), 1, orphan.Hash, world.Clock.GetUtcNow()), CancellationToken.None);

        ShouldBeRefusedAsInvalid(await world.RefreshAsync(orphan.Token));
    }

    [Fact]
    public async Task ALostRaceCountsAsReuseAndEndsTheChain()
    {
        var clients = new InMemoryClients();
        var users = new InMemoryUsers();
        var userId = (await users.GetOrCreateByIdentityAsync(IdentityProvider.Email, "pat@example.com", DateTimeOffset.UnixEpoch, CancellationToken.None)).User.Id;
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var (token, hash) = RefreshTokenRules.Generate();
        var stored = RefreshToken.StartChain(userId, 1, hash, now) with { Id = 7 };
        var repository = Substitute.For<IRefreshTokenRepository>();
        repository.FindByHashAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(stored);
        repository.RotateAsync(7, Arg.Any<RefreshToken>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        var handler = new RefreshTokensHandler(clients, repository, users, new RecordingTokenIssuer(), new TestClock(now));

        var result = await handler.HandleAsync(new RefreshTokensCommand("citybars", token), CancellationToken.None);

        ShouldBeRefusedAsInvalid(result);
        await repository.Received(1).RevokeChainAsync(stored.FamilyId, now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANewDisplayNameIsInTheNextAccessToken()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();
        world.Tokens.Issued[^1].User.DisplayName.ShouldBeNull("a new account has no name yet");

        await world.UpdateDisplayName.HandleAsync(new JTAuth.Application.Profile.UpdateDisplayNameCommand(world.UserIdOf(), "Pat"), CancellationToken.None);
        await world.RefreshAsync(tokens.RefreshToken);

        world.Tokens.Issued[^1].User.DisplayName.ShouldBe("Pat");
    }

    [Fact]
    public async Task LoggingOutEndsTheSessionSoItCanNoLongerBeRefreshed()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();

        var logout = await world.Logout.HandleAsync(new LogoutCommand(tokens.RefreshToken), CancellationToken.None);

        logout.IsSuccess.ShouldBeTrue();
        ShouldBeRefusedAsInvalid(await world.RefreshAsync(tokens.RefreshToken));
    }

    [Fact]
    public async Task LoggingOutAfterARefreshEndsTheChainThroughTheNewestToken()
    {
        var world = new SignInWorld();
        var first = await world.SignInAsync();
        var current = (await world.RefreshAsync(first.RefreshToken)).Value!;

        await world.Logout.HandleAsync(new LogoutCommand(current.RefreshToken), CancellationToken.None);

        world.RefreshTokens.All.ShouldAllBe(token => token.RevokedUtc != null);
        ShouldBeRefusedAsInvalid(await world.RefreshAsync(current.RefreshToken));
    }

    [Fact]
    public async Task LoggingOutEndsOnlyThatSession()
    {
        var world = new SignInWorld();
        var phone = await world.SignInAsync();
        var laptop = await world.SignInAsync();

        await world.Logout.HandleAsync(new LogoutCommand(phone.RefreshToken), CancellationToken.None);

        (await world.RefreshAsync(laptop.RefreshToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task LoggingOutAnUnknownOrAlreadyLoggedOutTokenStillSucceeds()
    {
        var world = new SignInWorld();
        var tokens = await world.SignInAsync();

        (await world.Logout.HandleAsync(new LogoutCommand(RefreshTokenRules.Generate().Token), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await world.Logout.HandleAsync(new LogoutCommand(tokens.RefreshToken), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await world.Logout.HandleAsync(new LogoutCommand(tokens.RefreshToken), CancellationToken.None)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ARefreshOrLogoutRequestWithNothingOrAnOversizedTokenIsRefusedByTheValidators()
    {
        var cancellation = TestContext.Current.CancellationToken;

        (await new RefreshTokensValidator().ValidateAsync(new RefreshTokensCommand("citybars", ""), cancellation)).IsValid.ShouldBeFalse();
        (await new RefreshTokensValidator().ValidateAsync(new RefreshTokensCommand("", "abc"), cancellation)).IsValid.ShouldBeFalse();
        (await new RefreshTokensValidator().ValidateAsync(new RefreshTokensCommand("citybars", new string('a', 201)), cancellation)).IsValid.ShouldBeFalse();
        (await new RefreshTokensValidator().ValidateAsync(new RefreshTokensCommand("citybars", new string('a', 43)), cancellation)).IsValid.ShouldBeTrue();
        (await new LogoutValidator().ValidateAsync(new LogoutCommand(""), cancellation)).IsValid.ShouldBeFalse();
        (await new LogoutValidator().ValidateAsync(new LogoutCommand(new string('a', 201)), cancellation)).IsValid.ShouldBeFalse();
    }
}
