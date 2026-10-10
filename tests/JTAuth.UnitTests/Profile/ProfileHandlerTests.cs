using JTAuth.Application;
using JTAuth.Application.Profile;
using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks;
using JTAuth.Contracts;
using JTAuth.Domain;
using JTAuth.UnitTests.SignIn;
using Shouldly;

namespace JTAuth.UnitTests.Profile;

public sealed class ProfileHandlerTests
{
    private static Task<Result<ProfileDto>> Get(SignInWorld world, Guid userId) =>
        world.GetProfile.HandleAsync(new GetProfileQuery(userId), CancellationToken.None);

    private static Task<Result<ProfileDto>> Rename(SignInWorld world, Guid userId, string name) =>
        world.UpdateDisplayName.HandleAsync(new UpdateDisplayNameCommand(userId, name), CancellationToken.None);

    private static Task<Result<CodeRequestedDto>> AskToChangeEmail(SignInWorld world, Guid userId, string newEmail, string audience = "citybars") =>
        world.RequestEmailChange.HandleAsync(new RequestEmailChangeCommand(userId, audience, newEmail), CancellationToken.None);

    private static Task<Result<ProfileDto>> ConfirmEmailChange(SignInWorld world, Guid userId, string newEmail, string code) =>
        world.ConfirmEmailChange.HandleAsync(new ConfirmEmailChangeCommand(userId, newEmail, code), CancellationToken.None);

    [Fact]
    public async Task TheProfileShowsTheNameAndHowThePersonCanSignIn()
    {
        var world = new SignInWorld();
        await world.SignInAsync("pat@example.com");

        var profile = (await Get(world, world.UserIdOf())).Value!;

        profile.DisplayName.ShouldBeNull();
        profile.SignInMethods.ShouldBe(
        [
            new SignInMethodDto("Email", "Linked", "pat@example.com"),
            new SignInMethodDto("Google", "NotLinked", null),
            new SignInMethodDto("Phone", "ComingSoon", null),
        ]);
    }

    [Fact]
    public async Task AGoogleIdentityShowsAsLinkedWithoutRevealingItsSubject()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        world.Users.LinkGoogle(world.UserIdOf(), "google-subject-123");

        var profile = (await Get(world, world.UserIdOf())).Value!;

        profile.SignInMethods.Single(method => method.Provider == "Google").ShouldBe(new SignInMethodDto("Google", "Linked", null));
    }

    [Fact]
    public async Task AGoogleOnlyAccountShowsEmailAsNotLinked()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        world.Users.UnlinkEmail(userId);
        world.Users.LinkGoogle(userId, "g-1");

        var profile = (await Get(world, userId)).Value!;

        profile.SignInMethods.Single(method => method.Provider == "Email").ShouldBe(new SignInMethodDto("Email", "NotLinked", null));
    }

    [Fact]
    public async Task AnUnknownPersonIsToldToSignInAgain()
    {
        var world = new SignInWorld();

        var result = await Get(world, Guid.NewGuid());

        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        result.Error.Code.ShouldBe(ProfileMapping.UnknownUserCode);
    }

    [Fact]
    public async Task ChangingTheDisplayNameTrimsItAndShowsItInTheProfile()
    {
        var world = new SignInWorld();
        await world.SignInAsync();

        var result = await Rename(world, world.UserIdOf(), "  Pat Smith ");

        result.Value!.DisplayName.ShouldBe("Pat Smith");
        (await Get(world, world.UserIdOf())).Value!.DisplayName.ShouldBe("Pat Smith");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    public async Task ABlankOrMultiLineNameIsRefusedAndTheOldNameKept(string name)
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        await Rename(world, world.UserIdOf(), "Pat");

        var result = await Rename(world, world.UserIdOf(), name);

        result.Error!.Type.ShouldBe(ResultErrorType.Validation);
        (await Get(world, world.UserIdOf())).Value!.DisplayName.ShouldBe("Pat");
    }

    [Fact]
    public async Task ANameOverFiftyCharactersIsRefused()
    {
        var world = new SignInWorld();
        await world.SignInAsync();

        (await Rename(world, world.UserIdOf(), new string('a', 51))).IsFailure.ShouldBeTrue();
        (await Rename(world, world.UserIdOf(), new string('a', 50))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RenamingAPersonWhoDoesNotExistIsRefused()
    {
        var world = new SignInWorld();

        (await Rename(world, Guid.NewGuid(), "Pat")).Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
    }

    [Fact]
    public async Task TheDisplayNameValidatorMirrorsTheRules()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var validator = new UpdateDisplayNameValidator();

        (await validator.ValidateAsync(new UpdateDisplayNameCommand(Guid.NewGuid(), "Pat"), cancellation)).IsValid.ShouldBeTrue();
        (await validator.ValidateAsync(new UpdateDisplayNameCommand(Guid.NewGuid(), " "), cancellation)).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new UpdateDisplayNameCommand(Guid.NewGuid(), new string('a', 51)), cancellation)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangingTheEmailSendsACodeToTheNewAddressNamingTheAppOfTheToken()
    {
        var world = new SignInWorld();
        await world.SignInAsync("pat@example.com", "otherapp");

        var result = await AskToChangeEmail(world, world.UserIdOf(), "  New.Pat@Example.com ", "otherapp.example");

        result.IsSuccess.ShouldBeTrue();
        world.Email.Last.To.ShouldBe("new.pat@example.com");
        world.Email.Last.AppName.ShouldBe("Other App");
    }

    [Fact]
    public async Task TheEmailDoesNotChangeUntilTheCodeIsEntered()
    {
        var world = new SignInWorld();
        await world.SignInAsync();

        await AskToChangeEmail(world, world.UserIdOf(), "new@example.com");

        (await Get(world, world.UserIdOf())).Value!.SignInMethods[0].Address.ShouldBe("pat@example.com");
    }

    [Fact]
    public async Task TheRightCodeReplacesTheEmail()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        await AskToChangeEmail(world, userId, "new@example.com");

        var result = await ConfirmEmailChange(world, userId, "NEW@example.com", world.Email.Last.Code);

        result.Value!.SignInMethods[0].ShouldBe(new SignInMethodDto("Email", "Linked", "new@example.com"));
        (await world.Users.FindOwnerAsync(IdentityProvider.Email, "new@example.com", CancellationToken.None)).ShouldBe(userId);
        (await world.Users.FindOwnerAsync(IdentityProvider.Email, "pat@example.com", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AfterTheChangeTheNewAddressSignsInToTheSameAccountAndTheOldOneStartsANewAccount()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        await AskToChangeEmail(world, userId, "new@example.com");
        await ConfirmEmailChange(world, userId, "new@example.com", world.Email.Last.Code);
        world.Clock.Advance(TimeSpan.FromMinutes(2));

        var withNew = await world.SignInAsync("new@example.com");
        var withOld = await world.SignInAsync("pat@example.com");

        withNew.IsNewUser.ShouldBeFalse();
        world.Tokens.Issued.Count(issued => issued.User.Id == userId).ShouldBe(2, "the first sign-in and the one with the new address");
        withOld.IsNewUser.ShouldBeTrue("nobody owns the old address any more");
        world.UserIdOf("pat@example.com").ShouldNotBe(userId);
    }

    [Fact]
    public async Task AWrongCodeLeavesTheEmailAsItWas()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        await AskToChangeEmail(world, userId, "new@example.com");

        var result = await ConfirmEmailChange(world, userId, "new@example.com", SignInWorld.WrongCodeFor(world.Email.Last.Code));

        result.Error!.Type.ShouldBe(ResultErrorType.Validation);
        result.Error.Code.ShouldBe(VerifyLoginCodeHandler.InvalidCodeCode);
        (await Get(world, userId)).Value!.SignInMethods[0].Address.ShouldBe("pat@example.com");
    }

    [Fact]
    public async Task ACodeSentToAnotherAddressCannotConfirmTheChange()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        await AskToChangeEmail(world, userId, "new@example.com");
        var code = world.Email.Last.Code;

        var result = await ConfirmEmailChange(world, userId, "someone.else@example.com", code);

        result.IsFailure.ShouldBeTrue();
        (await Get(world, userId)).Value!.SignInMethods[0].Address.ShouldBe("pat@example.com");
    }

    [Fact]
    public async Task ACodeWorksOnlyOnceForTheChange()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        await AskToChangeEmail(world, userId, "new@example.com");
        var code = world.Email.Last.Code;

        (await ConfirmEmailChange(world, userId, "new@example.com", code)).IsSuccess.ShouldBeTrue();
        (await ConfirmEmailChange(world, userId, "new@example.com", code)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task AnAddressThatAnotherAccountOwnsGetsTheSameAnswerButNoCodeIsSent()
    {
        var world = new SignInWorld();
        await world.SignInAsync("pat@example.com");
        await world.SignInAsync("sam@example.com");
        world.Email.Sent.Clear();
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        var pat = world.UserIdOf("pat@example.com");

        var taken = await AskToChangeEmail(world, pat, "sam@example.com");
        var free = await AskToChangeEmail(world, pat, "free@example.com");

        taken.IsSuccess.ShouldBeTrue();
        free.IsSuccess.ShouldBeTrue();
        taken.Value.ShouldBe(free.Value, "the answer must not reveal that the address has an account");
        world.Email.Sent.Select(sent => sent.To).ShouldBe(["free@example.com"]);
    }

    [Fact]
    public async Task AnAddressTakenInTheMeantimeIsRefusedWhenTheCodeIsEntered()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        await AskToChangeEmail(world, userId, "new@example.com");
        await world.Users.GetOrCreateByIdentityAsync(IdentityProvider.Email, "new@example.com", world.Clock.GetUtcNow(), CancellationToken.None);

        var result = await ConfirmEmailChange(world, userId, "new@example.com", world.Email.Last.Code);

        result.Error!.Type.ShouldBe(ResultErrorType.Conflict);
        result.Error.Code.ShouldBe(ConfirmEmailChangeHandler.EmailInUseCode);
        (await Get(world, userId)).Value!.SignInMethods[0].Address.ShouldBe("pat@example.com");
    }

    [Fact]
    public async Task AskingToChangeToYourOwnAddressIsRefused()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        world.Email.Sent.Clear();

        var result = await AskToChangeEmail(world, world.UserIdOf(), "PAT@example.com");

        result.Error!.Type.ShouldBe(ResultErrorType.Validation);
        result.Error.Code.ShouldBe(RequestEmailChangeHandler.AlreadyYoursCode);
        world.Email.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenForAnUnregisteredOrDisabledAppCannotStartAChange()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();

        (await AskToChangeEmail(world, userId, "new@example.com", "nosuchaudience")).Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        (await AskToChangeEmail(world, userId, "new@example.com", "retired")).Error!.Type.ShouldBe(ResultErrorType.Forbidden);
        world.Email.Sent.Count.ShouldBe(1, "only the sign-in code was sent");
    }

    [Fact]
    public async Task TheEmailChangeCodesAreLimitedLikeSignInCodes()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();

        (await AskToChangeEmail(world, userId, "new@example.com")).IsSuccess.ShouldBeTrue();
        var second = await AskToChangeEmail(world, userId, "new@example.com");

        second.Error!.Type.ShouldBe(ResultErrorType.TooManyRequests);
    }

    [Fact]
    public async Task APersonWithNoEmailYetCanAddOne()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        world.Users.UnlinkEmail(userId);
        world.Users.LinkGoogle(userId, "g-1");
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await AskToChangeEmail(world, userId, "first@example.com");

        var result = await ConfirmEmailChange(world, userId, "first@example.com", world.Email.Last.Code);

        result.Value!.SignInMethods.Single(method => method.Provider == "Email").ShouldBe(new SignInMethodDto("Email", "Linked", "first@example.com"));
        result.Value.SignInMethods.Single(method => method.Provider == "Google").Status.ShouldBe("Linked");
    }

    [Fact]
    public async Task TheEmailChangeValidatorsRefuseABadAddressOrCode()
    {
        var cancellation = TestContext.Current.CancellationToken;

        (await new RequestEmailChangeValidator().ValidateAsync(new RequestEmailChangeCommand(Guid.NewGuid(), "citybars", "nope"), cancellation)).IsValid.ShouldBeFalse();
        (await new RequestEmailChangeValidator().ValidateAsync(new RequestEmailChangeCommand(Guid.NewGuid(), "citybars", "ok@example.com"), cancellation)).IsValid.ShouldBeTrue();
        (await new ConfirmEmailChangeValidator().ValidateAsync(new ConfirmEmailChangeCommand(Guid.NewGuid(), "ok@example.com", "12345"), cancellation)).IsValid.ShouldBeFalse();
        (await new ConfirmEmailChangeValidator().ValidateAsync(new ConfirmEmailChangeCommand(Guid.NewGuid(), "ok@example.com", "123456"), cancellation)).IsValid.ShouldBeTrue();
    }
}
