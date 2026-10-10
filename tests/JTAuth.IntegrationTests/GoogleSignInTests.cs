using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JTAuth.Application;
using JTAuth.Contracts;
using JTAuth.Domain;
using JTAuth.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace JTAuth.IntegrationTests;

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class GoogleUserRepositoryTests(SqlServerDatabase database)
{
    private static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";

    private static string NewSubject() => "g-" + Guid.NewGuid().ToString("N");

    private UserRepository Repository() => new(new JTAuthDatabase(database.ConnectionString));

    [Fact]
    public async Task ANewGoogleAccountCreatesAPersonWithGoogleAndEmailIdentitiesAndTheGoogleName()
    {
        database.SkipIfUnavailable();
        var subject = NewSubject();
        var email = NewAddress();

        var outcome = await Repository().GetOrCreateByGoogleAsync(subject, email, "Pat Smith", Now(), TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(GoogleSignInStatus.Created);
        outcome.User!.DisplayName.ShouldBe("Pat Smith");
        (await Repository().GetIdentitiesAsync(outcome.User.Id, TestContext.Current.CancellationToken)).ShouldBe(
            [new UserIdentity(IdentityProvider.Google, subject), new UserIdentity(IdentityProvider.Email, email)], ignoreOrder: true);
        (await Repository().FindByIdAsync(outcome.User.Id, TestContext.Current.CancellationToken))!.DisplayName.ShouldBe("Pat Smith");
    }

    [Fact]
    public async Task TheSameGoogleAccountAgainSignsInToTheSamePersonAndCreatesNothing()
    {
        database.SkipIfUnavailable();
        var subject = NewSubject();
        var email = NewAddress();
        var first = await Repository().GetOrCreateByGoogleAsync(subject, email, "Pat", Now(), TestContext.Current.CancellationToken);

        var second = await Repository().GetOrCreateByGoogleAsync(subject, email, "Pat", Now(), TestContext.Current.CancellationToken);

        second.Status.ShouldBe(GoogleSignInStatus.SignedIn);
        second.User!.Id.ShouldBe(first.User!.Id);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE UserId = @id;", new { id = first.User.Id })).ShouldBe(2);
    }

    [Fact]
    public async Task ARenamedGoogleAddressIsStillFoundBySubject()
    {
        database.SkipIfUnavailable();
        var subject = NewSubject();
        var first = await Repository().GetOrCreateByGoogleAsync(subject, NewAddress(), "Pat", Now(), TestContext.Current.CancellationToken);

        var second = await Repository().GetOrCreateByGoogleAsync(subject, NewAddress(), "Pat", Now(), TestContext.Current.CancellationToken);

        second.Status.ShouldBe(GoogleSignInStatus.SignedIn);
        second.User!.Id.ShouldBe(first.User!.Id);
    }

    [Fact]
    public async Task AGoogleAccountWithTheVerifiedEmailOfAnEmailAccountLinksToThatAccount()
    {
        database.SkipIfUnavailable();
        var email = NewAddress();
        var (existing, _) = await Repository().GetOrCreateByIdentityAsync(IdentityProvider.Email, email, Now(), TestContext.Current.CancellationToken);
        await Repository().UpdateDisplayNameAsync(existing.Id, "Original Name", TestContext.Current.CancellationToken);
        var subject = NewSubject();

        var outcome = await Repository().GetOrCreateByGoogleAsync(subject, email, "Google Name", Now(), TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(GoogleSignInStatus.Linked);
        outcome.User!.Id.ShouldBe(existing.Id);
        outcome.User.DisplayName.ShouldBe("Original Name", "Google's name is used only for a new account");
        (await Repository().GetIdentitiesAsync(existing.Id, TestContext.Current.CancellationToken)).ShouldBe(
            [new UserIdentity(IdentityProvider.Email, email), new UserIdentity(IdentityProvider.Google, subject)], ignoreOrder: true);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject IN (@email, @subject);", new { email, subject })).ShouldBe(2, "no second account");
    }

    [Fact]
    public async Task ADifferentGoogleAccountForAnEmailThatAlreadyHasOneIsRefusedAndNothingChanges()
    {
        database.SkipIfUnavailable();
        var email = NewAddress();
        var first = await Repository().GetOrCreateByGoogleAsync(NewSubject(), email, "Pat", Now(), TestContext.Current.CancellationToken);
        var otherSubject = NewSubject();

        var outcome = await Repository().GetOrCreateByGoogleAsync(otherSubject, email, "Someone", Now(), TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(GoogleSignInStatus.Conflict);
        outcome.User.ShouldBeNull();
        (await Repository().FindOwnerAsync(IdentityProvider.Google, otherSubject, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await Repository().GetIdentitiesAsync(first.User!.Id, TestContext.Current.CancellationToken)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task AfterAGoogleSignUpTheSameAddressReachesTheSamePersonWithACode()
    {
        database.SkipIfUnavailable();
        var email = NewAddress();
        var google = await Repository().GetOrCreateByGoogleAsync(NewSubject(), email, "Pat", Now(), TestContext.Current.CancellationToken);

        var (viaCode, isNew) = await Repository().GetOrCreateByIdentityAsync(IdentityProvider.Email, email, Now(), TestContext.Current.CancellationToken);

        isNew.ShouldBeFalse();
        viaCode.Id.ShouldBe(google.User!.Id);
    }

    [Fact]
    public async Task ManyFirstSignInsOfOneGoogleAccountAtOnceMakeExactlyOnePerson()
    {
        database.SkipIfUnavailable();
        var subject = NewSubject();
        var email = NewAddress();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(
            () => Repository().GetOrCreateByGoogleAsync(subject, email, "Pat", Now(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        outcomes.Count(outcome => outcome.Status == GoogleSignInStatus.Created).ShouldBe(1);
        outcomes.Select(outcome => outcome.User!.Id).Distinct().Count().ShouldBe(1);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject IN (@email, @subject);", new { email, subject })).ShouldBe(2);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.[User] u WHERE NOT EXISTS (SELECT 1 FROM dbo.UserIdentity i WHERE i.UserId = u.Id);")).ShouldBe(0, "a lost race must not leave an orphan person behind");
    }

    [Fact]
    public async Task AGoogleSignUpAndACodeSignUpForTheSameAddressAtOnceEndUpAsOnePerson()
    {
        database.SkipIfUnavailable();
        var subject = NewSubject();
        var email = NewAddress();

        var google = Enumerable.Range(0, 5).Select(_ => Task.Run(
            async () => (await Repository().GetOrCreateByGoogleAsync(subject, email, "Pat", Now(), TestContext.Current.CancellationToken)).User!.Id, TestContext.Current.CancellationToken));
        var code = Enumerable.Range(0, 5).Select(_ => Task.Run(
            async () => (await Repository().GetOrCreateByIdentityAsync(IdentityProvider.Email, email, Now(), TestContext.Current.CancellationToken)).User.Id, TestContext.Current.CancellationToken));
        var userIds = await Task.WhenAll(google.Concat(code));

        userIds.Distinct().Count().ShouldBe(1, "one address is one account, however it is first used");
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject = @email;", new { email })).ShouldBe(1);
    }
}

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class GoogleSignInEndToEndTests(SqlServerDatabase database) : IDisposable
{
    private readonly string keyFile = Path.Combine(Path.GetTempPath(), "jtauth-e2e-" + Guid.NewGuid().ToString("N"), "signing-key.json");

    public void Dispose()
    {
        if (Path.GetDirectoryName(keyFile) is { } folder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";

    private static string NewSubject() => "g-" + Guid.NewGuid().ToString("N");

    private static Task<HttpResponseMessage> GoogleAsync(HttpClient client, string idToken, string clientId = "citybars") =>
        client.PostAsJsonAsync("/api/v1/auth/google", new GoogleSignInRequest(clientId, idToken), Cancel);

    private static async Task<AuthTokensDto> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<AuthTokensDto>(Cancel))!;
    }

    private static async Task<AuthTokensDto> SignInWithCodeAsync(JTAuthApi api, HttpClient client, string email)
    {
        (await client.PostAsJsonAsync("/api/v1/auth/code/request", new RequestCodeRequest("citybars", email), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = api.Emails.Sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).Code;
        return await ReadAsync(await client.PostAsJsonAsync("/api/v1/auth/code/verify", new VerifyCodeRequest("citybars", email, code), Cancel));
    }

    private static async Task<ProfileDto> ProfileAsync(HttpClient client, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ProfileDto>(Cancel))!;
    }

    [Fact]
    public async Task AGoogleSignInCreatesAnAccountAndReturnsTokensWithTheGoogleNameInThem()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();

        var tokens = await ReadAsync(await GoogleAsync(client, api.Google.Accept(NewSubject(), email, "Pat Smith")));

        tokens.IsNewUser.ShouldBeTrue();
        tokens.RefreshToken.Length.ShouldBe(43);
        var access = new JsonWebToken(tokens.AccessToken);
        access.Audiences.ShouldBe(["citybars"]);
        access.GetClaim("name").Value.ShouldBe("Pat Smith");
        var profile = await ProfileAsync(client, tokens.AccessToken);
        profile.DisplayName.ShouldBe("Pat Smith");
        profile.SignInMethods.Single(method => method.Provider == "Google").Status.ShouldBe("Linked");
        profile.SignInMethods.Single(method => method.Provider == "Email").Value.ShouldBe(email);
    }

    [Fact]
    public async Task AGoogleSignInWithTheVerifiedEmailOfAnExistingEmailAccountSignsInToThatSameAccount()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var byCode = await SignInWithCodeAsync(api, client, email);

        var byGoogle = await ReadAsync(await GoogleAsync(client, api.Google.Accept(NewSubject(), email.ToUpperInvariant(), "Google Name")));

        byGoogle.IsNewUser.ShouldBeFalse("it is the account the code sign-in made");
        new JsonWebToken(byGoogle.AccessToken).Subject.ShouldBe(new JsonWebToken(byCode.AccessToken).Subject);
        (await ProfileAsync(client, byGoogle.AccessToken)).SignInMethods.Single(method => method.Provider == "Google").Status.ShouldBe("Linked");
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject = @email;", new { email })).ShouldBe(1);
    }

    [Fact]
    public async Task AfterAGoogleSignUpTheSameAddressSignsInWithACodeToTheSameAccount()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var byGoogle = await ReadAsync(await GoogleAsync(client, api.Google.Accept(NewSubject(), email, "Pat")));

        var byCode = await SignInWithCodeAsync(api, client, email);

        byCode.IsNewUser.ShouldBeFalse();
        new JsonWebToken(byCode.AccessToken).Subject.ShouldBe(new JsonWebToken(byGoogle.AccessToken).Subject);
    }

    [Fact]
    public async Task SigningInWithTheSameGoogleAccountAgainIsTheSameAccount()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var token = api.Google.Accept(NewSubject(), NewAddress(), "Pat");

        var first = await ReadAsync(await GoogleAsync(client, token));
        var second = await ReadAsync(await GoogleAsync(client, token));

        first.IsNewUser.ShouldBeTrue();
        second.IsNewUser.ShouldBeFalse();
        new JsonWebToken(second.AccessToken).Subject.ShouldBe(new JsonWebToken(first.AccessToken).Subject);
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
    }

    [Fact]
    public async Task AGoogleSignInIsRecordedInUserClientAndStartsARefreshChain()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();

        var tokens = await ReadAsync(await GoogleAsync(client, api.Google.Accept(NewSubject(), email, "Pat")));

        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.UserClient uc JOIN dbo.UserIdentity i ON i.UserId = uc.UserId WHERE i.ProviderSubject = @email AND uc.ClientId = 1;", new { email })).ShouldBe(1);
        using var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("citybars", tokens.RefreshToken), Cancel);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ATokenGoogleDoesNotVouchForIsRefused()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();

        using var response = await GoogleAsync(client, "a.forged.token");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ADifferentGoogleAccountForAnAddressThatAlreadyHasOneIsAConflict()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        await ReadAsync(await GoogleAsync(client, api.Google.Accept(NewSubject(), email, "Pat")));

        using var response = await GoogleAsync(client, api.Google.Accept(NewSubject(), email, "Someone"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AnUnknownOrDisabledClientOrAMalformedRequestIsRefused()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var disabled = "off" + Guid.NewGuid().ToString("N")[..10];
        await database.ExecuteAsync("INSERT dbo.Client (ClientId, Name, Audience, IsEnabled) VALUES (@disabled, N'Off', 'off', 0);", new { disabled });
        var token = api.Google.Accept(NewSubject(), NewAddress(), "Pat");

        (await GoogleAsync(client, token, "nosuchapp")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GoogleAsync(client, token, disabled)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GoogleAsync(client, "")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GoogleAsync(client, new string('a', 5000))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WithNoGoogleClientIdConfiguredTheEndpointIsNotOffered()
    {
        database.SkipIfUnavailable();
        // The development settings list one id; a blank one in its place counts as no id at all.
        using var api = new JTAuthApi(database.ConnectionString, keyFile, new Dictionary<string, string> { ["JTAuth:GoogleClientIds:0"] = " " });
        using var client = api.CreateClient();

        using var response = await GoogleAsync(client, api.Google.Accept(NewSubject(), NewAddress(), "Pat"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
