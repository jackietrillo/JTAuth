using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks;
using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.SignIn;

public sealed class RequestLoginCodeHandlerTests
{
    private static Task<Result<JTAuth.Contracts.CodeRequestedDto>> Ask(SignInWorld world, string address = "pat@example.com", string clientId = "citybars") =>
        world.Request.HandleAsync(new RequestLoginCodeCommand(clientId, address), CancellationToken.None);

    [Fact]
    public async Task EmailsTheCodeAndNamesTheRequestingApp()
    {
        var world = new SignInWorld();

        var result = await Ask(world);

        result.IsSuccess.ShouldBeTrue();
        world.Email.Sent.Count.ShouldBe(1);
        world.Email.Last.To.ShouldBe("pat@example.com");
        world.Email.Last.AppName.ShouldBe("CityBars");
        LoginCodeRules.IsWellFormed(world.Email.Last.Code).ShouldBeTrue();
        world.Email.Last.ExpiresUtc.ShouldBe(world.Clock.GetUtcNow().AddMinutes(10));
    }

    [Fact]
    public async Task EmailNamesEachAppByItsOwnName()
    {
        var world = new SignInWorld();

        await Ask(world, clientId: "otherapp");

        world.Email.Last.AppName.ShouldBe("Other App");
    }

    [Fact]
    public async Task StoresOnlyAHashOfTheCode()
    {
        var world = new SignInWorld();

        await Ask(world);

        var stored = world.Codes.All.Single();
        stored.CodeHash.ShouldBe(LoginCodeRules.Hash("test-secret", IdentityProvider.Email, "pat@example.com", world.Email.Last.Code));
        Convert.ToHexString(stored.CodeHash).ShouldNotContain(world.Email.Last.Code);
        stored.ExpiresUtc.ShouldBe(world.Clock.GetUtcNow().AddMinutes(10));
        stored.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task NormalizesTheAddressBeforeStoringAndSending()
    {
        var world = new SignInWorld();

        await Ask(world, "  Pat@Example.COM ");

        world.Email.Last.To.ShouldBe("pat@example.com");
        world.Codes.All.Single().ProviderSubject.ShouldBe("pat@example.com");
    }

    [Fact]
    public async Task AnswersTheSameForAnAddressWithAnAccountAndOneWithout()
    {
        var world = new SignInWorld();
        var knownCode = await world.RequestCodeAsync("known@example.com");
        (await world.VerifyAsync(knownCode, "known@example.com")).IsSuccess.ShouldBeTrue();
        world.Users.All.Count.ShouldBe(1);
        world.Clock.Advance(TimeSpan.FromMinutes(2));

        var known = await Ask(world, "known@example.com");
        var unknown = await Ask(world, "stranger@example.com");

        known.IsSuccess.ShouldBeTrue();
        unknown.IsSuccess.ShouldBeTrue();
        known.Value.ShouldBe(unknown.Value);
        world.Email.Sent.Count.ShouldBe(3, "an unknown address gets a code too; nothing is sent differently");
    }

    [Fact]
    public void NeverAsksWhetherTheAddressHasAnAccount()
    {
        // The handler has no way to reach the user store at all, so it cannot answer differently for known addresses.
        typeof(RequestLoginCodeHandler).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ShouldNotContain(typeof(JTAuth.Application.IUserRepository));
    }

    [Fact]
    public async Task RefusesAnUnknownClientAndSendsNothing()
    {
        var world = new SignInWorld();

        var result = await Ask(world, clientId: "nosuchapp");

        result.Error!.Type.ShouldBe(ResultErrorType.Validation);
        result.Error.Code.ShouldBe(ClientCheck.UnknownClientCode);
        world.Email.Sent.ShouldBeEmpty();
        world.Codes.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task RefusesADisabledClientAndSendsNothing()
    {
        var world = new SignInWorld();

        var result = await Ask(world, clientId: "retired");

        result.Error!.Type.ShouldBe(ResultErrorType.Forbidden);
        result.Error.Code.ShouldBe(ClientCheck.DisabledClientCode);
        world.Email.Sent.ShouldBeEmpty();
        world.Codes.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task AllowsOneCodePerMinutePerAddress()
    {
        var world = new SignInWorld();
        (await Ask(world)).IsSuccess.ShouldBeTrue();

        world.Clock.Advance(TimeSpan.FromSeconds(59));
        var tooSoon = await Ask(world);

        tooSoon.Error!.Type.ShouldBe(ResultErrorType.TooManyRequests);
        world.Email.Sent.Count.ShouldBe(1);

        world.Clock.Advance(TimeSpan.FromSeconds(2));
        (await Ask(world)).IsSuccess.ShouldBeTrue();
        world.Email.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AllowsFiveCodesAnHourPerAddress()
    {
        var world = new SignInWorld();
        for (var index = 0; index < 5; index++)
        {
            (await Ask(world)).IsSuccess.ShouldBeTrue($"request {index + 1}");
            world.Clock.Advance(TimeSpan.FromMinutes(2));
        }

        var sixth = await Ask(world);

        sixth.Error!.Type.ShouldBe(ResultErrorType.TooManyRequests);
        world.Email.Sent.Count.ShouldBe(5);

        world.Clock.Advance(TimeSpan.FromMinutes(51));
        (await Ask(world)).IsSuccess.ShouldBeTrue("the oldest requests have left the hour");
    }

    [Fact]
    public async Task TheLimitIsPerAddressNotPerApp()
    {
        var world = new SignInWorld();
        (await Ask(world, clientId: "citybars")).IsSuccess.ShouldBeTrue();

        (await Ask(world, clientId: "otherapp")).Error!.Type.ShouldBe(ResultErrorType.TooManyRequests);
        (await Ask(world, "sam@example.com", "otherapp")).IsSuccess.ShouldBeTrue("another address is not affected");
    }

    [Fact]
    public async Task ANewCodeInvalidatesThePreviousOne()
    {
        var world = new SignInWorld();
        var first = await world.RequestCodeAsync();
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        var second = await world.RequestCodeAsync();

        if (first == second)
        {
            // One in a million: the same digits twice. Ask again so the two differ and the check means something.
            world.Clock.Advance(TimeSpan.FromMinutes(2));
            second = await world.RequestCodeAsync();
        }

        (await world.VerifyAsync(first)).Error!.Code.ShouldBe(VerifyLoginCodeHandler.InvalidCodeCode);
        (await world.VerifyAsync(second)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task OnlyTheNewestCodeStaysOpen()
    {
        var world = new SignInWorld();
        await world.RequestCodeAsync();
        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await world.RequestCodeAsync();

        world.Codes.All.Count(code => code.ConsumedUtc is null).ShouldBe(1, "only the newest code is open");
        world.Codes.All[^1].ConsumedUtc.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "pat@example.com")]
    [InlineData("citybars", "")]
    [InlineData("citybars", "not-an-email")]
    [InlineData("citybars", "a@b@c.com")]
    public async Task TheValidatorRefusesAMissingClientOrBadAddress(string clientId, string email)
    {
        var validation = await new RequestLoginCodeValidator().ValidateAsync(new RequestLoginCodeCommand(clientId, email), TestContext.Current.CancellationToken);

        validation.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task TheValidatorAcceptsAGoodRequest()
    {
        var validation = await new RequestLoginCodeValidator().ValidateAsync(new RequestLoginCodeCommand("citybars", "  Pat@Example.com "), TestContext.Current.CancellationToken);

        validation.IsValid.ShouldBeTrue();
    }
}
