using JTAuth.Application;
using JTAuth.Domain;
using JTAuth.Infrastructure;
using Shouldly;

namespace JTAuth.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class RefreshTokenRepositoryTests(SqlServerDatabase database)
{
    private static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private RefreshTokenRepository Repository() => new(new JTAuthDatabase(database.ConnectionString));

    private async Task<Guid> NewUserAsync()
    {
        var (user, _) = await new UserRepository(new JTAuthDatabase(database.ConnectionString))
            .GetOrCreateByIdentityAsync(IdentityProvider.Email, $"{Guid.NewGuid():N}@example.com", Now(), TestContext.Current.CancellationToken);
        return user.Id;
    }

    /// <summary>Stores a new chain's first token and returns it as stored (with its id).</summary>
    private async Task<RefreshToken> StartChainAsync(Guid userId, DateTimeOffset now)
    {
        var first = RefreshToken.StartChain(userId, 1, RefreshTokenRules.Generate().Hash, now);
        await Repository().AddAsync(first, TestContext.Current.CancellationToken);
        return (await Repository().FindByHashAsync(first.TokenHash, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task StoresATokenByItsHashAndReadsItBackExactly()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var now = Now();
        var token = RefreshToken.StartChain(userId, 1, RefreshTokenRules.Generate().Hash, now);

        await Repository().AddAsync(token, TestContext.Current.CancellationToken);
        var stored = await Repository().FindByHashAsync(token.TokenHash, TestContext.Current.CancellationToken);

        stored!.Id.ShouldBeGreaterThan(0);
        stored.UserId.ShouldBe(userId);
        stored.ClientId.ShouldBe(1);
        stored.FamilyId.ShouldBe(token.FamilyId);
        stored.TokenHash.ShouldBe(token.TokenHash);
        stored.ExpiresUtc.ShouldBe(now.AddDays(30));
        stored.CreatedUtc.ShouldBe(now);
        stored.RevokedUtc.ShouldBeNull();
        stored.ExpiresUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task AnUnknownHashFindsNothing()
    {
        database.SkipIfUnavailable();

        (await Repository().FindByHashAsync(RefreshTokenRules.Generate().Hash, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task RotatingRevokesTheTokenAndStoresTheNextOneInTheSameChain()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var now = Now();
        var first = await StartChainAsync(userId, now);
        var next = first.Next(RefreshTokenRules.Generate().Hash, now.AddMinutes(15));

        var rotated = await Repository().RotateAsync(first.Id, next, now.AddMinutes(15), TestContext.Current.CancellationToken);

        rotated.ShouldBeTrue();
        var oldRow = (await Repository().FindByHashAsync(first.TokenHash, TestContext.Current.CancellationToken))!;
        var newRow = (await Repository().FindByHashAsync(next.TokenHash, TestContext.Current.CancellationToken))!;
        oldRow.RevokedUtc.ShouldBe(now.AddMinutes(15));
        newRow.RevokedUtc.ShouldBeNull();
        newRow.FamilyId.ShouldBe(first.FamilyId);
        newRow.ExpiresUtc.ShouldBe(now.AddMinutes(15).AddDays(30));
        (await database.ScalarAsync<long?>("SELECT ReplacedByTokenId FROM dbo.RefreshToken WHERE Id = @id;", new { id = first.Id })).ShouldBe(newRow.Id);
    }

    [Fact]
    public async Task OfManyCallersRotatingTheSameTokenExactlyOneSucceeds()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var now = Now();
        var first = await StartChainAsync(userId, now);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(
            () => Repository().RotateAsync(first.Id, first.Next(RefreshTokenRules.Generate().Hash, now.AddMinutes(1)), now.AddMinutes(1), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken)));

        results.Count(rotated => rotated).ShouldBe(1);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.RefreshToken WHERE FamilyId = @family;", new { family = first.FamilyId })).ShouldBe(2, "the token and one replacement, no strays from the losers");
    }

    [Fact]
    public async Task AnExpiredOrRevokedTokenCannotBeRotated()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var now = Now();
        var expiring = await StartChainAsync(userId, now);
        var revoked = await StartChainAsync(userId, now);
        await Repository().RevokeChainAsync(revoked.FamilyId, now, TestContext.Current.CancellationToken);

        (await Repository().RotateAsync(expiring.Id, expiring.Next(RefreshTokenRules.Generate().Hash, now), now.AddDays(30), TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await Repository().RotateAsync(revoked.Id, revoked.Next(RefreshTokenRules.Generate().Hash, now), now.AddMinutes(1), TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.RefreshToken WHERE FamilyId IN (@a, @b);", new { a = expiring.FamilyId, b = revoked.FamilyId })).ShouldBe(2, "a refused rotation stores nothing");
        (await Repository().RotateAsync(expiring.Id, expiring.Next(RefreshTokenRules.Generate().Hash, now), now.AddDays(30).AddMilliseconds(-1), TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task EndingAChainRevokesItsLiveTokensOnlyAndLeavesOtherChainsAlone()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var now = Now();
        var phone = await StartChainAsync(userId, now);
        var phoneNext = phone.Next(RefreshTokenRules.Generate().Hash, now.AddMinutes(1));
        await Repository().RotateAsync(phone.Id, phoneNext, now.AddMinutes(1), TestContext.Current.CancellationToken);
        var laptop = await StartChainAsync(userId, now);

        await Repository().RevokeChainAsync(phone.FamilyId, now.AddMinutes(5), TestContext.Current.CancellationToken);

        var phoneOld = (await Repository().FindByHashAsync(phone.TokenHash, TestContext.Current.CancellationToken))!;
        var phoneLive = (await Repository().FindByHashAsync(phoneNext.TokenHash, TestContext.Current.CancellationToken))!;
        var laptopRow = (await Repository().FindByHashAsync(laptop.TokenHash, TestContext.Current.CancellationToken))!;
        phoneOld.RevokedUtc.ShouldBe(now.AddMinutes(1), "a token revoked earlier keeps its own time");
        phoneLive.RevokedUtc.ShouldBe(now.AddMinutes(5));
        laptopRow.RevokedUtc.ShouldBeNull();
    }
}

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class UserProfileRepositoryTests(SqlServerDatabase database)
{
    private static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";

    private UserRepository Repository() => new(new JTAuthDatabase(database.ConnectionString));

    private async Task<(Guid UserId, string Address)> NewUserAsync()
    {
        var address = NewAddress();
        var (user, _) = await Repository().GetOrCreateByIdentityAsync(IdentityProvider.Email, address, Now(), TestContext.Current.CancellationToken);
        return (user.Id, address);
    }

    [Fact]
    public async Task FindsAPersonByIdAndReturnsNullForAnUnknownId()
    {
        database.SkipIfUnavailable();
        var (userId, _) = await NewUserAsync();

        (await Repository().FindByIdAsync(userId, TestContext.Current.CancellationToken))!.Id.ShouldBe(userId);
        (await Repository().FindByIdAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SetsTheDisplayNameAndReportsAnUnknownPerson()
    {
        database.SkipIfUnavailable();
        var (userId, _) = await NewUserAsync();

        (await Repository().UpdateDisplayNameAsync(userId, "Pat Smith", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await Repository().UpdateDisplayNameAsync(Guid.NewGuid(), "Nobody", TestContext.Current.CancellationToken)).ShouldBeFalse();

        (await Repository().FindByIdAsync(userId, TestContext.Current.CancellationToken))!.DisplayName.ShouldBe("Pat Smith");
    }

    [Fact]
    public async Task ListsTheWaysAPersonCanSignInAndWhoOwnsAnIdentity()
    {
        database.SkipIfUnavailable();
        var (userId, address) = await NewUserAsync();

        (await Repository().GetIdentitiesAsync(userId, TestContext.Current.CancellationToken)).ShouldBe([new UserIdentity(IdentityProvider.Email, address)]);
        (await Repository().FindOwnerAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken)).ShouldBe(userId);
        (await Repository().FindOwnerAsync(IdentityProvider.Email, NewAddress(), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await Repository().FindOwnerAsync(IdentityProvider.Google, address, TestContext.Current.CancellationToken)).ShouldBeNull("the same text under another provider is not the same identity");
    }

    [Fact]
    public async Task ChangingTheEmailMovesTheIdentityToTheNewAddress()
    {
        database.SkipIfUnavailable();
        var (userId, oldAddress) = await NewUserAsync();
        var newAddress = NewAddress();
        var now = Now().AddHours(1);

        var result = await Repository().ChangeEmailAsync(userId, newAddress, now, TestContext.Current.CancellationToken);

        result.ShouldBe(EmailChangeResult.Changed);
        (await Repository().FindOwnerAsync(IdentityProvider.Email, newAddress, TestContext.Current.CancellationToken)).ShouldBe(userId);
        (await Repository().FindOwnerAsync(IdentityProvider.Email, oldAddress, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await database.ScalarAsync<DateTime>("SELECT VerifiedUtc FROM dbo.UserIdentity WHERE ProviderSubject = @newAddress;", new { newAddress })).ShouldBe(now.UtcDateTime);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE UserId = @userId;", new { userId })).ShouldBe(1);
    }

    [Fact]
    public async Task AnAddressAnotherAccountOwnsIsRefusedAndNothingChanges()
    {
        database.SkipIfUnavailable();
        var (userId, address) = await NewUserAsync();
        var (_, takenAddress) = await NewUserAsync();

        var result = await Repository().ChangeEmailAsync(userId, takenAddress, Now(), TestContext.Current.CancellationToken);

        result.ShouldBe(EmailChangeResult.AddressTaken);
        (await Repository().FindOwnerAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken)).ShouldBe(userId);
    }

    [Fact]
    public async Task APersonWithoutAnEmailIdentityGainsOne()
    {
        database.SkipIfUnavailable();
        var (userId, address) = await NewUserAsync();
        await database.ExecuteAsync("DELETE dbo.UserIdentity WHERE UserId = @userId;", new { userId });
        var newAddress = NewAddress();

        var result = await Repository().ChangeEmailAsync(userId, newAddress, Now(), TestContext.Current.CancellationToken);

        result.ShouldBe(EmailChangeResult.Changed);
        (await Repository().GetIdentitiesAsync(userId, TestContext.Current.CancellationToken)).ShouldBe([new UserIdentity(IdentityProvider.Email, newAddress)]);
        (await Repository().FindOwnerAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ChangingToTheAddressTheyAlreadyHaveChangesNothingAndSucceeds()
    {
        database.SkipIfUnavailable();
        var (userId, address) = await NewUserAsync();

        (await Repository().ChangeEmailAsync(userId, address, Now(), TestContext.Current.CancellationToken)).ShouldBe(EmailChangeResult.Changed);
        (await Repository().FindOwnerAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken)).ShouldBe(userId);
    }

    [Fact]
    public async Task AnUnknownPersonCannotChangeAnEmail()
    {
        database.SkipIfUnavailable();

        (await Repository().ChangeEmailAsync(Guid.NewGuid(), NewAddress(), Now(), TestContext.Current.CancellationToken)).ShouldBe(EmailChangeResult.NoSuchUser);
    }

    [Fact]
    public async Task TwoPeopleChangingToTheSameAddressAtOnceOnlyOneGetsIt()
    {
        database.SkipIfUnavailable();
        var (first, _) = await NewUserAsync();
        var (second, _) = await NewUserAsync();
        var contested = NewAddress();

        var results = await Task.WhenAll(
            Task.Run(() => Repository().ChangeEmailAsync(first, contested, Now(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(() => Repository().ChangeEmailAsync(second, contested, Now(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        results.Count(result => result == EmailChangeResult.Changed).ShouldBe(1);
        results.Count(result => result == EmailChangeResult.AddressTaken).ShouldBe(1);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject = @contested;", new { contested })).ShouldBe(1);
    }
}

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ClientAudienceRepositoryTests(SqlServerDatabase database)
{
    [Fact]
    public async Task FindsTheAppWhoseTokensCarryAnAudience()
    {
        database.SkipIfUnavailable();
        var repository = new ClientRepository(new JTAuthDatabase(database.ConnectionString));

        (await repository.FindByAudienceAsync("citybars", TestContext.Current.CancellationToken))!.ClientId.ShouldBe("citybars");
        (await repository.FindByAudienceAsync("nosuchaudience", TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
