using JTAuth.Application.Profile;
using JTAuth.BuildingBlocks;
using JTAuth.Domain;
using JTAuth.UnitTests.SignIn;
using Shouldly;

namespace JTAuth.UnitTests.Profile;

/// <summary>Deleting an account needs a recent sign-in, removes the person when no other app has them, and otherwise only their place in this app.</summary>
public sealed class DeleteAccountHandlerTests
{
    private static Task<Result<JTAuth.Contracts.AccountDeletedDto>> Delete(SignInWorld world, Guid userId, DateTimeOffset? signedIn, string audience = "citybars") =>
        world.DeleteAccount.HandleAsync(new DeleteAccountCommand(userId, audience, signedIn), CancellationToken.None);

    [Fact]
    public async Task APersonWhoJustSignedInIsDeletedForGood()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();

        var result = await Delete(world, userId, world.Clock.GetUtcNow().AddMinutes(-3));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.AccountRemoved.ShouldBeTrue();
        world.Users.Deletions.ShouldBe([(userId, 1)]);
        (await world.Users.FindByIdAsync(userId, CancellationToken.None)).ShouldBeNull();
        (await world.Users.FindOwnerAsync(IdentityProvider.Email, "pat@example.com", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task APersonWhoAlsoUsesAnotherAppOnlyLeavesThisOne()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        world.Users.UseAnotherApp.Add(userId);

        var result = await Delete(world, userId, world.Clock.GetUtcNow());

        result.Value!.AccountRemoved.ShouldBeFalse();
        (await world.Users.FindByIdAsync(userId, CancellationToken.None)).ShouldNotBeNull("the other app still has the person");
    }

    [Fact]
    public async Task ASignInOlderThanTenMinutesIsRefusedAndNothingIsDeleted()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();

        var result = await Delete(world, userId, world.Clock.GetUtcNow().AddMinutes(-11));

        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(ResultErrorType.Forbidden);
        result.Error.Code.ShouldBe(DeleteAccountHandler.RecentSignInRequiredCode);
        world.Users.Deletions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenWithNoSignInTimeIsRefused()
    {
        var world = new SignInWorld();
        await world.SignInAsync();

        var result = await Delete(world, world.UserIdOf(), null);

        result.Error!.Code.ShouldBe(DeleteAccountHandler.RecentSignInRequiredCode);
        world.Users.Deletions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenForAnAppThatIsNotRegisteredIsRefused()
    {
        var world = new SignInWorld();
        await world.SignInAsync();

        var result = await Delete(world, world.UserIdOf(), world.Clock.GetUtcNow(), audience: "nobody");

        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        world.Users.Deletions.ShouldBeEmpty();
    }

    [Fact]
    public async Task APersonWhoIsAlreadyGoneIsToldToSignInAgain()
    {
        var world = new SignInWorld();

        var result = await Delete(world, Guid.NewGuid(), world.Clock.GetUtcNow());

        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        result.Error.Code.ShouldBe("unknown_user");
    }

    [Fact]
    public async Task DeletingTwiceTheSecondTimeFindsNoOne()
    {
        var world = new SignInWorld();
        await world.SignInAsync();
        var userId = world.UserIdOf();
        var signedIn = world.Clock.GetUtcNow();
        await Delete(world, userId, signedIn);

        var again = await Delete(world, userId, signedIn);

        again.IsFailure.ShouldBeTrue();
        again.Error!.Code.ShouldBe("unknown_user");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(600, false)]
    public void TheWindowIsTenMinutes(int minutesAgo, bool recent)
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        AccountRules.IsRecentSignIn(now.AddMinutes(-minutesAgo), now).ShouldBe(recent);
    }

    [Fact]
    public void NoSignInTimeAndATimeFromTheFutureAreNeverRecent()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        AccountRules.IsRecentSignIn(null, now).ShouldBeFalse();
        AccountRules.IsRecentSignIn(now.AddHours(1), now).ShouldBeFalse();
        AccountRules.IsRecentSignIn(now.AddSeconds(30), now).ShouldBeTrue("a little clock difference between servers is allowed");
    }
}
