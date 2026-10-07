using JTAuth.Domain;
using JTAuth.Infrastructure;
using Shouldly;

namespace JTAuth.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ClientRepositoryTests(SqlServerDatabase database)
{
    [Fact]
    public async Task FindsTheSeededCityBarsClient()
    {
        database.SkipIfUnavailable();

        var client = await new ClientRepository(new JTAuthDatabase(database.ConnectionString)).FindByClientIdAsync("citybars", TestContext.Current.CancellationToken);

        client.ShouldBe(new Client(1, "citybars", "CityBars", "citybars", true));
    }

    [Fact]
    public async Task ReturnsNullForAnUnknownClient()
    {
        database.SkipIfUnavailable();

        (await new ClientRepository(new JTAuthDatabase(database.ConnectionString)).FindByClientIdAsync("nosuchapp", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ReportsADisabledClientAsDisabled()
    {
        database.SkipIfUnavailable();
        var clientId = "off" + Guid.NewGuid().ToString("N")[..10];
        await database.ExecuteAsync("INSERT dbo.Client (ClientId, Name, Audience, IsEnabled) VALUES (@clientId, N'Off', 'off', 0);", new { clientId });

        var client = await new ClientRepository(new JTAuthDatabase(database.ConnectionString)).FindByClientIdAsync(clientId, TestContext.Current.CancellationToken);

        client!.IsEnabled.ShouldBeFalse();
    }
}

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class LoginCodeRepositoryTests(SqlServerDatabase database)
{
    private static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";

    private static LoginCode Issue(string address, DateTimeOffset now, byte fill = 1) =>
        LoginCode.Issue(IdentityProvider.Email, address, Enumerable.Repeat(fill, 32).ToArray(), now);

    private LoginCodeRepository Repository() => new(new JTAuthDatabase(database.ConnectionString));

    [Fact]
    public async Task StoresTheCodeHashAndReadsItBackExactly()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var now = Now();
        var issued = Issue(address, now, fill: 7);

        await Repository().ReplaceAsync(issued, now, TestContext.Current.CancellationToken);
        var stored = await Repository().FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken);

        stored!.Id.ShouldBeGreaterThan(0);
        stored.CodeHash.ShouldBe(issued.CodeHash);
        stored.ExpiresUtc.ShouldBe(now.AddMinutes(10));
        stored.CreatedUtc.ShouldBe(now);
        stored.AttemptCount.ShouldBe(0);
        stored.ConsumedUtc.ShouldBeNull();
        stored.ExpiresUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task ANewCodeEndsTheOlderOpenOnesAndIsTheLatest()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var now = Now();
        var repository = Repository();

        await repository.ReplaceAsync(Issue(address, now, 1), now, TestContext.Current.CancellationToken);
        var first = (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!;
        await repository.ReplaceAsync(Issue(address, now.AddMinutes(2), 2), now.AddMinutes(2), TestContext.Current.CancellationToken);
        var second = (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!;

        second.Id.ShouldBeGreaterThan(first.Id);
        second.CodeHash[0].ShouldBe((byte)2);
        second.ConsumedUtc.ShouldBeNull();
        (await repository.TryConsumeAsync(first.Id, now.AddMinutes(3), TestContext.Current.CancellationToken)).ShouldBeFalse("the older code can no longer be redeemed");
        (await repository.TryClaimAttemptAsync(first.Id, 5, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await repository.TryConsumeAsync(second.Id, now.AddMinutes(3), TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task CodesOfOtherAddressesAreLeftAlone()
    {
        database.SkipIfUnavailable();
        var mine = NewAddress();
        var theirs = NewAddress();
        var now = Now();
        var repository = Repository();
        await repository.ReplaceAsync(Issue(theirs, now), now, TestContext.Current.CancellationToken);

        await repository.ReplaceAsync(Issue(mine, now.AddMinutes(1)), now.AddMinutes(1), TestContext.Current.CancellationToken);

        (await repository.FindLatestAsync(IdentityProvider.Email, theirs, TestContext.Current.CancellationToken))!.ConsumedUtc.ShouldBeNull();
        (await repository.FindLatestAsync(IdentityProvider.Email, NewAddress(), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task CountsOnlyCodesIssuedSinceTheGivenMoment()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var start = Now();
        var repository = Repository();
        foreach (var minutes in new[] { 0, 10, 20 })
        {
            await repository.ReplaceAsync(Issue(address, start.AddMinutes(minutes)), start.AddMinutes(minutes), TestContext.Current.CancellationToken);
        }

        (await repository.CountIssuedSinceAsync(IdentityProvider.Email, address, start.AddMinutes(-1), TestContext.Current.CancellationToken)).ShouldBe(3);
        (await repository.CountIssuedSinceAsync(IdentityProvider.Email, address, start.AddMinutes(5), TestContext.Current.CancellationToken)).ShouldBe(2);
        (await repository.CountIssuedSinceAsync(IdentityProvider.Email, address, start.AddMinutes(20), TestContext.Current.CancellationToken)).ShouldBe(0, "the boundary is exclusive");
        (await repository.CountIssuedSinceAsync(IdentityProvider.Email, NewAddress(), start.AddMinutes(-1), TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task AllowsExactlyFiveAttemptsPerCode()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var now = Now();
        var repository = Repository();
        await repository.ReplaceAsync(Issue(address, now), now, TestContext.Current.CancellationToken);
        var id = (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!.Id;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            (await repository.TryClaimAttemptAsync(id, LoginCodeRules.MaxAttempts, TestContext.Current.CancellationToken)).ShouldBeTrue($"attempt {attempt}");
        }

        (await repository.TryClaimAttemptAsync(id, LoginCodeRules.MaxAttempts, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!.AttemptCount.ShouldBe(5);
    }

    [Fact]
    public async Task ParallelGuessesCannotExceedTheAttemptLimit()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var now = Now();
        var repository = Repository();
        await repository.ReplaceAsync(Issue(address, now), now, TestContext.Current.CancellationToken);
        var id = (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!.Id;

        var claims = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(
            () => repository.TryClaimAttemptAsync(id, LoginCodeRules.MaxAttempts, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        claims.Count(granted => granted).ShouldBe(5);
        (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!.AttemptCount.ShouldBe(5);
    }

    [Fact]
    public async Task ACodeCanBeConsumedByExactlyOneCaller()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var now = Now();
        var repository = Repository();
        await repository.ReplaceAsync(Issue(address, now), now, TestContext.Current.CancellationToken);
        var id = (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!.Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(
            () => repository.TryConsumeAsync(id, now.AddMinutes(1), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        results.Count(consumed => consumed).ShouldBe(1);
        (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!.ConsumedUtc.ShouldBe(now.AddMinutes(1));
        (await repository.TryClaimAttemptAsync(id, 5, TestContext.Current.CancellationToken)).ShouldBeFalse("a used code takes no more attempts");
    }

    [Fact]
    public async Task AnExpiredCodeCannotBeConsumed()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var now = Now();
        var repository = Repository();
        await repository.ReplaceAsync(Issue(address, now), now, TestContext.Current.CancellationToken);
        var id = (await repository.FindLatestAsync(IdentityProvider.Email, address, TestContext.Current.CancellationToken))!.Id;

        (await repository.TryConsumeAsync(id, now.AddMinutes(10), TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await repository.TryConsumeAsync(id, now.AddMinutes(10).AddMilliseconds(-1), TestContext.Current.CancellationToken)).ShouldBeTrue();
    }
}

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class UserRepositoryTests(SqlServerDatabase database)
{
    private static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";

    private UserRepository Repository() => new(new JTAuthDatabase(database.ConnectionString));

    [Fact]
    public async Task CreatesAPersonWithAnEmailIdentityAtTheFirstSignIn()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var now = Now();

        var (user, isNew) = await Repository().GetOrCreateByIdentityAsync(IdentityProvider.Email, address, now, TestContext.Current.CancellationToken);

        isNew.ShouldBeTrue();
        user.Id.ShouldNotBe(Guid.Empty);
        user.DisplayName.ShouldBeNull();
        var identity = (await database.QueryAsync<(Guid UserId, string Provider, DateTime VerifiedUtc)>(
            "SELECT UserId, Provider, VerifiedUtc FROM dbo.UserIdentity WHERE ProviderSubject = @address;", new { address })).Single();
        identity.UserId.ShouldBe(user.Id);
        identity.Provider.ShouldBe("Email");
        identity.VerifiedUtc.ShouldBe(now.UtcDateTime);
    }

    [Fact]
    public async Task ASecondSignInReachesTheSamePersonAndIsNotNew()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var repository = Repository();

        var (first, firstIsNew) = await repository.GetOrCreateByIdentityAsync(IdentityProvider.Email, address, Now(), TestContext.Current.CancellationToken);
        var (second, secondIsNew) = await repository.GetOrCreateByIdentityAsync(IdentityProvider.Email, address, Now(), TestContext.Current.CancellationToken);

        firstIsNew.ShouldBeTrue();
        secondIsNew.ShouldBeFalse();
        second.Id.ShouldBe(first.Id);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject = @address;", new { address })).ShouldBe(1);
    }

    [Fact]
    public async Task TwoAddressesAreTwoPeople()
    {
        database.SkipIfUnavailable();
        var repository = Repository();

        var (one, _) = await repository.GetOrCreateByIdentityAsync(IdentityProvider.Email, NewAddress(), Now(), TestContext.Current.CancellationToken);
        var (two, _) = await repository.GetOrCreateByIdentityAsync(IdentityProvider.Email, NewAddress(), Now(), TestContext.Current.CancellationToken);

        two.Id.ShouldNotBe(one.Id);
    }

    [Fact]
    public async Task ConcurrentFirstSignInsOfOneAddressMakeExactlyOnePerson()
    {
        database.SkipIfUnavailable();
        var address = NewAddress();
        var repository = Repository();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(
            () => repository.GetOrCreateByIdentityAsync(IdentityProvider.Email, address, Now(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        results.Count(result => result.IsNew).ShouldBe(1);
        results.Select(result => result.User.Id).Distinct().Count().ShouldBe(1);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject = @address;", new { address })).ShouldBe(1);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.[User] u WHERE NOT EXISTS (SELECT 1 FROM dbo.UserIdentity i WHERE i.UserId = u.Id);")).ShouldBe(0, "a lost race must not leave an orphan person behind");
    }
}

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class UserClientRepositoryTests(SqlServerDatabase database)
{
    private static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private async Task<Guid> NewUserAsync()
    {
        var (user, _) = await new UserRepository(new JTAuthDatabase(database.ConnectionString))
            .GetOrCreateByIdentityAsync(IdentityProvider.Email, $"{Guid.NewGuid():N}@example.com", Now(), TestContext.Current.CancellationToken);
        return user.Id;
    }

    private UserClientRepository Repository() => new(new JTAuthDatabase(database.ConnectionString));

    private async Task<(DateTime First, DateTime Last, DateTime? Consent, DateTime? Revoked)> RowAsync(Guid userId, int clientId)
    {
        var rows = await database.QueryAsync<(DateTime First, DateTime Last, DateTime? Consent, DateTime? Revoked)>(
            "SELECT FirstSignInUtc, LastSignInUtc, ContactConsentUtc, RevokedUtc FROM dbo.UserClient WHERE UserId = @userId AND ClientId = @clientId;",
            new { userId, clientId });
        return rows.Single();
    }

    [Fact]
    public async Task TheFirstSignInCreatesTheRowWithBothTimesSet()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var now = Now();

        await Repository().RecordSignInAsync(userId, 1, now, TestContext.Current.CancellationToken);

        var row = await RowAsync(userId, 1);
        row.First.ShouldBe(now.UtcDateTime);
        row.Last.ShouldBe(now.UtcDateTime);
        row.Consent.ShouldBeNull();
        row.Revoked.ShouldBeNull();
    }

    [Fact]
    public async Task LaterSignInsMoveTheLastSignInAndKeepTheFirst()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var first = Now();
        var repository = Repository();

        await repository.RecordSignInAsync(userId, 1, first, TestContext.Current.CancellationToken);
        await repository.RecordSignInAsync(userId, 1, first.AddHours(2), TestContext.Current.CancellationToken);
        await repository.RecordSignInAsync(userId, 1, first.AddDays(3), TestContext.Current.CancellationToken);

        var row = await RowAsync(userId, 1);
        row.First.ShouldBe(first.UtcDateTime);
        row.Last.ShouldBe(first.AddDays(3).UtcDateTime);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserClient WHERE UserId = @userId;", new { userId })).ShouldBe(1);
    }

    [Fact]
    public async Task ASignInNeverMovesTheLastSignInBackwards()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var first = Now();
        var repository = Repository();

        await repository.RecordSignInAsync(userId, 1, first, TestContext.Current.CancellationToken);
        await repository.RecordSignInAsync(userId, 1, first.AddHours(1), TestContext.Current.CancellationToken);
        await repository.RecordSignInAsync(userId, 1, first.AddMinutes(30), TestContext.Current.CancellationToken);

        (await RowAsync(userId, 1)).Last.ShouldBe(first.AddHours(1).UtcDateTime);
    }

    [Fact]
    public async Task SigningInNeverTouchesContactConsentOrRevocation()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var first = Now();
        var repository = Repository();
        await repository.RecordSignInAsync(userId, 1, first, TestContext.Current.CancellationToken);
        var consent = first.AddMinutes(5).UtcDateTime;
        var revoked = first.AddMinutes(6).UtcDateTime;
        await database.ExecuteAsync(
            "UPDATE dbo.UserClient SET ContactConsentUtc = @consent, RevokedUtc = @revoked WHERE UserId = @userId AND ClientId = 1;", new { userId, consent, revoked });

        await repository.RecordSignInAsync(userId, 1, first.AddHours(1), TestContext.Current.CancellationToken);

        var row = await RowAsync(userId, 1);
        row.Consent.ShouldBe(consent);
        row.Revoked.ShouldBe(revoked);
        row.Last.ShouldBe(first.AddHours(1).UtcDateTime);
    }

    [Fact]
    public async Task EachAppHasItsOwnRow()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var otherClientId = await database.ScalarAsync<int>(
            "INSERT dbo.Client (ClientId, Name, Audience) OUTPUT inserted.Id VALUES (@id, N'Second', @id);", new { id = "app" + Guid.NewGuid().ToString("N")[..10] });
        var now = Now();
        var repository = Repository();

        await repository.RecordSignInAsync(userId, 1, now, TestContext.Current.CancellationToken);
        await repository.RecordSignInAsync(userId, otherClientId, now.AddHours(1), TestContext.Current.CancellationToken);

        (await RowAsync(userId, 1)).Last.ShouldBe(now.UtcDateTime);
        (await RowAsync(userId, otherClientId)).First.ShouldBe(now.AddHours(1).UtcDateTime);
    }

    [Fact]
    public async Task ConcurrentFirstSignInsLeaveOneRow()
    {
        database.SkipIfUnavailable();
        var userId = await NewUserAsync();
        var repository = Repository();
        var now = Now();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(
            () => repository.RecordSignInAsync(userId, 1, now.AddMilliseconds(index), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserClient WHERE UserId = @userId;", new { userId })).ShouldBe(1);
        var row = await RowAsync(userId, 1);
        row.Last.ShouldBeGreaterThanOrEqualTo(row.First);
    }
}
