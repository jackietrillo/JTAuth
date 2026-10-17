using JTAuth.Application.Contacts;
using JTAuth.BuildingBlocks;
using JTAuth.Contracts;
using JTAuth.Domain;
using JTAuth.UnitTests.SignIn;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace JTAuth.UnitTests.Contacts;

/// <summary>An app's server may ask for the emails of its own users, identified by its service key, and never anyone else's.</summary>
public sealed class LookupContactsHandlerTests
{
    private const string CityBarsKey = "citybars-service-key-for-tests";
    private const string OtherAppKey = "other-app-service-key-for-tests";
    private static readonly Guid Pat = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Sam = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid NoEmail = new("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid OtherAppOnly = new("00000000-0000-0000-0000-0000000000a4");

    private static (LookupContactsHandler Handler, SignInWorld World) Build()
    {
        var world = new SignInWorld();
        world.Clients.SetServiceKey("citybars", CityBarsKey);
        world.Clients.SetServiceKey("otherapp", OtherAppKey);
        world.Clients.SetServiceKey("retired", "retired-app-key-for-tests");
        world.UserClients.Known[(1, Pat)] = "pat@example.com";
        world.UserClients.Known[(1, Sam)] = "sam@example.com";
        world.UserClients.Known[(1, NoEmail)] = null;
        world.UserClients.Known[(2, OtherAppOnly)] = "other@example.com";
        return (new LookupContactsHandler(world.Clients, world.UserClients, NullLogger<LookupContactsHandler>.Instance), world);
    }

    private static Task<Result<ContactLookupDto>> Ask(LookupContactsHandler handler, string? key, params Guid[] ids) =>
        handler.HandleAsync(new LookupContactsQuery(key, ids), CancellationToken.None);

    [Fact]
    public async Task TheKeyedAppGetsTheEmailsOfItsOwnPeople()
    {
        var (handler, _) = Build();

        var result = await Ask(handler, CityBarsKey, Pat, Sam);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBe([new ContactLookupItemDto(Pat, "pat@example.com"), new ContactLookupItemDto(Sam, "sam@example.com")], ignoreOrder: true);
    }

    [Fact]
    public async Task AnAccountWithNoEmailIsListedWithNone()
    {
        var (handler, _) = Build();

        var result = await Ask(handler, CityBarsKey, NoEmail);

        result.Value!.Items.ShouldBe([new ContactLookupItemDto(NoEmail, null)]);
    }

    [Fact]
    public async Task SomeoneWhoNeverSignedInToTheAppIsLeftOutWhoeverTheyAre()
    {
        var (handler, _) = Build();

        var result = await Ask(handler, CityBarsKey, Pat, OtherAppOnly, Guid.NewGuid());

        result.Value!.Items.Select(item => item.UserId).ShouldBe([Pat]);
    }

    [Fact]
    public async Task AnotherAppsKeyOnlyEverSeesThatAppsPeople()
    {
        var (handler, _) = Build();

        var result = await Ask(handler, OtherAppKey, Pat, Sam, OtherAppOnly);

        result.Value!.Items.ShouldBe([new ContactLookupItemDto(OtherAppOnly, "other@example.com")]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-key")]
    public async Task AMissingOrUnknownKeyIsRefusedAndLearnsNothing(string? key)
    {
        var (handler, _) = Build();

        var result = await Ask(handler, key, Pat);

        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
        result.Error.Code.ShouldBe("invalid_service_key");
    }

    [Fact]
    public async Task TheKeyIsCheckedBeforeTheRequestSoABadKeyWithBadIdsIsStillJustRefused()
    {
        var (handler, _) = Build();

        var result = await handler.HandleAsync(new LookupContactsQuery("not-a-key", []), CancellationToken.None);

        result.Error!.Type.ShouldBe(ResultErrorType.Unauthorized);
    }

    [Fact]
    public async Task ADisabledAppIsForbiddenEvenWithItsRealKey()
    {
        var (handler, _) = Build();

        var result = await Ask(handler, "retired-app-key-for-tests", Pat);

        result.Error!.Type.ShouldBe(ResultErrorType.Forbidden);
    }

    [Fact]
    public async Task TheKeyAroundTheSpacesOfAHeaderStillMatches()
    {
        var (handler, _) = Build();

        (await Ask(handler, $"  {CityBarsKey}  ", Pat)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task NoIdsOnlyEmptyIdsOrTooManyIdsAreRefused()
    {
        var (handler, _) = Build();

        (await Ask(handler, CityBarsKey)).Error!.Type.ShouldBe(ResultErrorType.Validation);
        (await Ask(handler, CityBarsKey, Guid.Empty)).Error!.Type.ShouldBe(ResultErrorType.Validation);
        (await handler.HandleAsync(new LookupContactsQuery(CityBarsKey, null), CancellationToken.None)).Error!.Type.ShouldBe(ResultErrorType.Validation);

        var tooMany = Enumerable.Range(0, LookupContactsHandler.MaxUserIds + 1).Select(_ => Guid.NewGuid()).ToArray();
        (await Ask(handler, CityBarsKey, tooMany)).Error!.Type.ShouldBe(ResultErrorType.Validation);

        var justEnough = Enumerable.Range(0, LookupContactsHandler.MaxUserIds).Select(_ => Guid.NewGuid()).ToArray();
        (await Ask(handler, CityBarsKey, justEnough)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TheSamePersonAskedTwiceIsAnsweredOnce()
    {
        var (handler, _) = Build();

        var result = await Ask(handler, CityBarsKey, Pat, Pat, Pat);

        result.Value!.Items.Count.ShouldBe(1);
    }

    [Fact]
    public void AKeyIsStoredOnlyAsAHashThatIgnoresSurroundingSpacesAndIsNeverEmpty()
    {
        ServiceKeyRules.Hash(null).ShouldBeNull();
        ServiceKeyRules.Hash("  ").ShouldBeNull();
        ServiceKeyRules.Hash("abc")!.Length.ShouldBe(32);
        ServiceKeyRules.Hash(" abc ").ShouldBe(ServiceKeyRules.Hash("abc"));
        ServiceKeyRules.Hash("abc").ShouldNotBe(ServiceKeyRules.Hash("abd"));
    }
}