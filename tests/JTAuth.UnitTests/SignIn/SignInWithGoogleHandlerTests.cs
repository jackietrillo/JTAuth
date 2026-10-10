using JTAuth.Application;
using JTAuth.Application.Profile;
using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks;
using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.SignIn;

public sealed class SignInWithGoogleHandlerTests
{
    private static readonly CancellationToken Cancel = CancellationToken.None;

    [Fact]
    public async Task AGoogleSignInWithNoMatchingAccountCreatesOneWithBothIdentitiesAndGoogleName()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", "Pat Smith");

        var result = await world.GoogleAsync(token);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.IsNewUser.ShouldBeTrue();
        result.Value.RefreshToken.Length.ShouldBe(43);
        var userId = world.UserIdOf("pat@example.com");
        (await world.Users.FindByIdAsync(userId, Cancel))!.DisplayName.ShouldBe("Pat Smith");
        var identities = await world.Users.GetIdentitiesAsync(userId, Cancel);
        identities.ShouldContain(new UserIdentity(IdentityProvider.Google, "g-100"));
        identities.ShouldContain(new UserIdentity(IdentityProvider.Email, "pat@example.com"));
    }

    [Fact]
    public async Task ASecondGoogleSignInReachesTheSameAccountAndIsNotNew()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", "Pat");
        var first = await world.GoogleAsync(token);

        var second = await world.GoogleAsync(token);

        first.Value!.IsNewUser.ShouldBeTrue();
        second.Value!.IsNewUser.ShouldBeFalse();
        world.Users.All.Count.ShouldBe(1);
        world.RefreshTokens.All.Select(stored => stored.FamilyId).Distinct().Count().ShouldBe(2, "each sign-in starts its own chain");
    }

    [Fact]
    public async Task AGoogleAccountWithTheVerifiedEmailOfAnExistingEmailAccountSignsInToThatAccount()
    {
        var world = new SignInWorld();
        await world.SignInAsync("pat@example.com");
        var existingId = world.UserIdOf("pat@example.com");
        var token = world.Google.Accept("t1", "g-100", "Pat@Example.COM", "A Different Name");

        var result = await world.GoogleAsync(token);

        result.Value!.IsNewUser.ShouldBeFalse("it is the account that already existed");
        world.Users.All.Count.ShouldBe(1, "no second account");
        (await world.Users.FindOwnerAsync(IdentityProvider.Google, "g-100", Cancel)).ShouldBe(existingId);
        world.Tokens.Issued[^1].User.Id.ShouldBe(existingId);
        (await world.Users.FindByIdAsync(existingId, Cancel))!.DisplayName.ShouldBeNull("Google's name is used only for a new account");
        var profile = (await world.GetProfile.HandleAsync(new GetProfileQuery(existingId), Cancel)).Value!;
        profile.SignInMethods.Single(method => method.Provider == "Google").Status.ShouldBe("Linked");
        profile.SignInMethods.Single(method => method.Provider == "Email").Address.ShouldBe("pat@example.com");
    }

    [Fact]
    public async Task AfterAGoogleSignUpTheSameAddressSignsInWithACodeToTheSameAccount()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", "Pat");
        await world.GoogleAsync(token);
        var googleUserId = world.UserIdOf("pat@example.com");

        var withCode = await world.SignInAsync("pat@example.com");

        withCode.IsNewUser.ShouldBeFalse();
        world.Users.All.Count.ShouldBe(1);
        world.Tokens.Issued[^1].User.Id.ShouldBe(googleUserId);
    }

    [Fact]
    public async Task AGoogleAccountIsFoundBySubjectEvenWhenItsEmailHasChanged()
    {
        var world = new SignInWorld();
        var first = world.Google.Accept("t1", "g-100", "old@example.com", "Pat");
        await world.GoogleAsync(first);
        var second = world.Google.Accept("t2", "g-100", "new@example.com", "Pat");

        var result = await world.GoogleAsync(second);

        result.Value!.IsNewUser.ShouldBeFalse();
        world.Users.All.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ADifferentGoogleAccountForAnAddressThatAlreadyHasOneIsRefused()
    {
        var world = new SignInWorld();
        await world.GoogleAsync(world.Google.Accept("t1", "g-100", "pat@example.com", "Pat"));
        var tokensBefore = world.Tokens.Issued.Count;
        var other = world.Google.Accept("t2", "g-999", "pat@example.com", "Someone Else");

        var result = await world.GoogleAsync(other);

        result.Error!.Type.ShouldBe(ResultErrorType.Conflict);
        result.Error.Code.ShouldBe(SignInWithGoogleHandler.AccountConflictCode);
        world.Tokens.Issued.Count.ShouldBe(tokensBefore);
        (await world.Users.FindOwnerAsync(IdentityProvider.Google, "g-999", Cancel)).ShouldBeNull();
    }

    [Fact]
    public async Task EveryGoogleSignInIsRecordedAgainstThePersonAndTheApp()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", "Pat");

        await world.GoogleAsync(token);
        world.Clock.Advance(TimeSpan.FromHours(2));
        await world.GoogleAsync(token);

        var userId = world.UserIdOf("pat@example.com");
        world.UserClients.SignIns.Select(signIn => (signIn.UserId, signIn.ClientId)).ShouldBe([(userId, 1), (userId, 1)]);
    }

    [Fact]
    public async Task ATokenIsIssuedForTheRequestingClientsAudience()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", "Pat");

        await world.GoogleAsync(token, "otherapp");

        world.Tokens.Issued.Single().Client.Audience.ShouldBe("otherapp.example");
    }

    [Fact]
    public async Task ATokenGoogleDoesNotVouchForIsRefusedAndCreatesNothing()
    {
        var world = new SignInWorld();

        var result = await world.GoogleAsync("forged-or-expired-or-for-another-app");

        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        result.Error.Code.ShouldBe(SignInWithGoogleHandler.InvalidTokenCode);
        world.Users.All.ShouldBeEmpty();
        world.RefreshTokens.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEmailAddressThatIsNotUsableIsRefused()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "not-an-address", "Pat");

        (await world.GoogleAsync(token)).Error!.Code.ShouldBe(SignInWithGoogleHandler.InvalidTokenCode);
        world.Users.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithNoGoogleClientIdConfiguredGoogleSignInIsNotOfferedAndGoogleIsNotAsked()
    {
        var world = new SignInWorld();
        world.Settings.GoogleClientIds = [];
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", "Pat");

        var result = await world.GoogleAsync(token);

        result.Error!.Type.ShouldBe(ResultErrorType.NotFound);
        result.Error.Code.ShouldBe(SignInWithGoogleHandler.NotConfiguredCode);
        world.Google.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AnUnknownOrDisabledClientIsRefusedBeforeGoogleIsAsked()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", "Pat");

        var unknown = await world.GoogleAsync(token, "nosuchapp");
        var disabled = await world.GoogleAsync(token, "retired");

        unknown.Error!.Type.ShouldBe(ResultErrorType.Validation);
        disabled.Error!.Type.ShouldBe(ResultErrorType.Forbidden);
        world.Google.Calls.ShouldBe(0);
        world.Users.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALongGoogleNameIsCutToWhatADisplayNameAllows()
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", new string('a', 80));

        await world.GoogleAsync(token);

        (await world.Users.FindByIdAsync(world.UserIdOf("pat@example.com"), Cancel))!.DisplayName.ShouldBe(new string('a', 50));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pat\nSmith")]
    public async Task AMissingOrUnusableGoogleNameLeavesTheAccountWithoutOne(string? name)
    {
        var world = new SignInWorld();
        var token = world.Google.Accept("t1", "g-100", "pat@example.com", name);

        await world.GoogleAsync(token);

        (await world.Users.FindByIdAsync(world.UserIdOf("pat@example.com"), Cancel))!.DisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task TheValidatorRefusesAnEmptyOrOversizedRequest()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var validator = new SignInWithGoogleValidator();

        (await validator.ValidateAsync(new SignInWithGoogleCommand("citybars", "a.b.c"), cancellation)).IsValid.ShouldBeTrue();
        (await validator.ValidateAsync(new SignInWithGoogleCommand("citybars", ""), cancellation)).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new SignInWithGoogleCommand("", "a.b.c"), cancellation)).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new SignInWithGoogleCommand("citybars", new string('a', SignInWithGoogleValidator.MaxTokenLength + 1)), cancellation)).IsValid.ShouldBeFalse();
    }
}
