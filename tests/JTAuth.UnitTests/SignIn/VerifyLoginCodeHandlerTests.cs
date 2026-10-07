using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks;
using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.SignIn;

public sealed class VerifyLoginCodeHandlerTests
{
    private static void ShouldBeRejectedAsInvalidCode<T>(Result<T> result)
    {
        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        result.Error.Code.ShouldBe(VerifyLoginCodeHandler.InvalidCodeCode);
    }

    [Fact]
    public async Task TheRightCodeSignsAnUnknownAddressUpAsANewUser()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        var result = await world.VerifyAsync(code);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsNewUser.ShouldBeTrue();
        result.Value.AccessToken.ShouldNotBeNullOrWhiteSpace();
        result.Value.AccessTokenExpiresUtc.ShouldBe(world.Clock.GetUtcNow().AddMinutes(15));
        world.Users.All.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SigningInAgainReachesTheSameAccountAndIsNotNew()
    {
        var world = new SignInWorld();
        var first = await world.VerifyAsync(await world.RequestCodeAsync());
        world.Clock.Advance(TimeSpan.FromMinutes(2));

        var second = await world.VerifyAsync(await world.RequestCodeAsync());

        first.Value!.IsNewUser.ShouldBeTrue();
        second.Value!.IsNewUser.ShouldBeFalse();
        world.Users.All.Count.ShouldBe(1);
        world.Tokens.Issued.Select(issued => issued.User.Id).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task DifferentSpellingsOfOneAddressAreOneAccount()
    {
        var world = new SignInWorld();
        var first = await world.VerifyAsync(await world.RequestCodeAsync("Pat@Example.com"), "Pat@Example.com");
        world.Clock.Advance(TimeSpan.FromMinutes(2));

        var second = await world.VerifyAsync(await world.RequestCodeAsync("  pat@EXAMPLE.COM "), "pat@example.com  ");

        first.Value!.IsNewUser.ShouldBeTrue();
        second.Value!.IsNewUser.ShouldBeFalse();
        world.Users.All.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AWrongCodeIsRejectedAndNoAccountIsCreated()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        ShouldBeRejectedAsInvalidCode(await world.VerifyAsync(SignInWorld.WrongCodeFor(code)));

        world.Users.All.ShouldBeEmpty();
        world.UserClients.SignIns.ShouldBeEmpty();
        world.Tokens.Issued.ShouldBeEmpty();
    }

    [Fact]
    public async Task AWrongCodeCountsAsAnAttemptAndTheRightCodeStillWorks()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        await world.VerifyAsync(SignInWorld.WrongCodeFor(code));

        world.Codes.All.Single().AttemptCount.ShouldBe(1);
        (await world.VerifyAsync(code)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TheFifthWrongAttemptKillsTheCodeEvenForTheRightDigits()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();
        var wrong = SignInWorld.WrongCodeFor(code);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            ShouldBeRejectedAsInvalidCode(await world.VerifyAsync(wrong));
        }

        ShouldBeRejectedAsInvalidCode(await world.VerifyAsync(code));
        world.Users.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheRightCodeOnTheFifthAttemptStillWorks()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();
        var wrong = SignInWorld.WrongCodeFor(code);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await world.VerifyAsync(wrong);
        }

        (await world.VerifyAsync(code)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ACodeWorksOnce()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        (await world.VerifyAsync(code)).IsSuccess.ShouldBeTrue();

        ShouldBeRejectedAsInvalidCode(await world.VerifyAsync(code));
        world.Tokens.Issued.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACodeIsRejectedOnceTenMinutesHavePassed()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        world.Clock.Advance(TimeSpan.FromMinutes(10));

        ShouldBeRejectedAsInvalidCode(await world.VerifyAsync(code));
        world.Users.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACodeStillWorksJustBeforeTenMinutes()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        world.Clock.Advance(TimeSpan.FromMinutes(10).Subtract(TimeSpan.FromSeconds(1)));

        (await world.VerifyAsync(code)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ANewCodeInvalidatesTheOldOne()
    {
        var world = new SignInWorld();
        var old = await world.RequestCodeAsync();
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        var current = await world.RequestCodeAsync();
        var stale = old == current ? SignInWorld.WrongCodeFor(current) : old;

        ShouldBeRejectedAsInvalidCode(await world.VerifyAsync(stale));
        (await world.VerifyAsync(current)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnAddressThatNeverAskedForACodeGetsTheSameAnswerAsAWrongCode()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync("pat@example.com");

        var neverAsked = await world.VerifyAsync("123456", "stranger@example.com");
        var wrong = await world.VerifyAsync(SignInWorld.WrongCodeFor(code), "pat@example.com");

        ShouldBeRejectedAsInvalidCode(neverAsked);
        ShouldBeRejectedAsInvalidCode(wrong);
        neverAsked.Error.ShouldBe(wrong.Error);
    }

    [Fact]
    public async Task ACodeForOneAddressDoesNotSignInAnother()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync("pat@example.com");
        await world.RequestCodeAsync("sam@example.com");

        ShouldBeRejectedAsInvalidCode(await world.VerifyAsync(code, "sam@example.com"));
    }

    [Fact]
    public async Task RefusesAnUnknownClient()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        var result = await world.VerifyAsync(code, clientId: "nosuchapp");

        result.Error!.Type.ShouldBe(ResultErrorType.Validation);
        result.Error.Code.ShouldBe(ClientCheck.UnknownClientCode);
        world.Users.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task RefusesADisabledClientEvenWithTheRightCode()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        var result = await world.VerifyAsync(code, clientId: "retired");

        result.Error!.Type.ShouldBe(ResultErrorType.Forbidden);
        result.Error.Code.ShouldBe(ClientCheck.DisabledClientCode);
        world.Users.All.ShouldBeEmpty();
        world.Codes.All.Single().ConsumedUtc.ShouldBeNull("a refused client must not burn the person's code");
    }

    [Fact]
    public async Task TheTokenIsIssuedForTheRequestingClientsAudience()
    {
        var world = new SignInWorld();

        await world.VerifyAsync(await world.RequestCodeAsync(clientId: "citybars"), clientId: "citybars");
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await world.VerifyAsync(await world.RequestCodeAsync(clientId: "otherapp"), clientId: "otherapp");

        world.Tokens.Issued.Select(issued => issued.Client.Audience).ShouldBe(["citybars", "otherapp.example"]);
    }

    [Fact]
    public async Task EverySuccessfulSignInIsRecordedAgainstThePersonAndTheApp()
    {
        var world = new SignInWorld();
        var firstSignInAt = world.Clock.GetUtcNow();
        await world.VerifyAsync(await world.RequestCodeAsync());
        world.Clock.Advance(TimeSpan.FromHours(3));
        var secondSignInAt = world.Clock.GetUtcNow();
        await world.VerifyAsync(await world.RequestCodeAsync());

        var userId = world.Users.All.Single().Id;
        world.UserClients.SignIns.ShouldBe([(userId, 1, firstSignInAt), (userId, 1, secondSignInAt)]);
    }

    [Fact]
    public async Task SigningInToASecondAppIsRecordedForThatApp()
    {
        var world = new SignInWorld();
        await world.VerifyAsync(await world.RequestCodeAsync(clientId: "citybars"), clientId: "citybars");
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await world.VerifyAsync(await world.RequestCodeAsync(clientId: "otherapp"), clientId: "otherapp");

        world.Users.All.Count.ShouldBe(1, "one account across every app");
        world.UserClients.SignIns.Select(signIn => signIn.ClientId).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task AFailedSignInIsNotRecorded()
    {
        var world = new SignInWorld();
        var code = await world.RequestCodeAsync();

        await world.VerifyAsync(SignInWorld.WrongCodeFor(code));

        world.UserClients.SignIns.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("citybars", "pat@example.com", "12345")]
    [InlineData("citybars", "pat@example.com", "1234567")]
    [InlineData("citybars", "pat@example.com", "abcdef")]
    [InlineData("citybars", "pat@example.com", "")]
    [InlineData("citybars", "nope", "123456")]
    [InlineData("", "pat@example.com", "123456")]
    public async Task TheValidatorRefusesAMalformedRequest(string clientId, string email, string code)
    {
        var validation = await new VerifyLoginCodeValidator().ValidateAsync(new VerifyLoginCodeCommand(clientId, email, code), TestContext.Current.CancellationToken);

        validation.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task TheValidatorAcceptsAWellFormedRequest()
    {
        var validation = await new VerifyLoginCodeValidator().ValidateAsync(new VerifyLoginCodeCommand("citybars", "pat@example.com", "012345"), TestContext.Current.CancellationToken);

        validation.IsValid.ShouldBeTrue();
    }
}
