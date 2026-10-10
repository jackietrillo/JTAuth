using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using JTAuth.Contracts;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace JTAuth.IntegrationTests;

/// <summary>Keeping a session going with refresh tokens, logging out, and the profile, over HTTP against a real database.</summary>
[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class SessionAndProfileEndToEndTests(SqlServerDatabase database) : IDisposable
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

    private static async Task<AuthTokensDto> SignInAsync(JTAuthApi api, HttpClient client, string email, string clientId = "citybars")
    {
        (await client.PostAsJsonAsync("/api/v1/auth/code/request", new RequestCodeRequest(clientId, email), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = api.Emails.Sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).Code;
        var response = await client.PostAsJsonAsync("/api/v1/auth/code/verify", new VerifyCodeRequest(clientId, email, code), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<AuthTokensDto>(Cancel))!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken, string clientId = "citybars") =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(clientId, refreshToken), Cancel);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? accessToken, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Cancel);
    }

    private static async Task<AuthTokensDto> ReadTokensAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<AuthTokensDto>(Cancel))!;
    }

    [Fact]
    public async Task TheSignInResponseCarriesBothTokensAndWhenTheyExpire()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        await client.PostAsJsonAsync("/api/v1/auth/code/request", new RequestCodeRequest("citybars", email), Cancel);

        using var response = await client.PostAsJsonAsync("/api/v1/auth/code/verify", new VerifyCodeRequest("citybars", email, api.Emails.Sent.Single().Code), Cancel);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));

        json.RootElement.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["accessToken", "accessTokenExpiresUtc", "isNewUser", "refreshToken", "refreshTokenExpiresUtc"]);
        json.RootElement.GetProperty("refreshTokenExpiresUtc").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(29));
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task RefreshingReturnsANewPairAndTheOldRefreshTokenStopsWorking()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var first = await SignInAsync(api, client, NewAddress());

        var second = await ReadTokensAsync(await RefreshAsync(client, first.RefreshToken));

        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        second.AccessToken.ShouldNotBe(first.AccessToken);
        second.IsNewUser.ShouldBeFalse();
        new JsonWebToken(second.AccessToken).Subject.ShouldBe(new JsonWebToken(first.AccessToken).Subject);
        new JsonWebToken(second.AccessToken).Audiences.ShouldBe(["citybars"]);
        var third = await ReadTokensAsync(await RefreshAsync(client, second.RefreshToken));
        third.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        using var again = await RefreshAsync(client, first.RefreshToken);
        again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReusingARevokedRefreshTokenRevokesTheWholeChain()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var first = await SignInAsync(api, client, NewAddress());
        var second = await ReadTokensAsync(await RefreshAsync(client, first.RefreshToken));

        using var reuse = await RefreshAsync(client, first.RefreshToken);
        using var newestNow = await RefreshAsync(client, second.RefreshToken);

        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        newestNow.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the chain was revoked, so even the newest token is dead");
    }

    [Fact]
    public async Task ADisplayNameChangeShowsUpInTheNameClaimOfTheNextAccessToken()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var first = await SignInAsync(api, client, NewAddress());
        new JsonWebToken(first.AccessToken).TryGetClaim("name", out _).ShouldBeFalse("a new account has no name yet");

        using var update = await SendAsync(client, HttpMethod.Put, "/api/v1/profile", first.AccessToken, new UpdateProfileRequest("  Pat Smith  "));
        var second = await ReadTokensAsync(await RefreshAsync(client, first.RefreshToken));

        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<ProfileDto>(Cancel))!.DisplayName.ShouldBe("Pat Smith");
        new JsonWebToken(second.AccessToken).GetClaim("name").Value.ShouldBe("Pat Smith");
    }

    [Fact]
    public async Task TheProfileShowsTheNameAndTheSignInMethods()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var tokens = await SignInAsync(api, client, email);

        using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/profile", tokens.AccessToken);
        var profile = (await response.Content.ReadFromJsonAsync<ProfileDto>(Cancel))!;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        profile.DisplayName.ShouldBeNull();
        profile.SignInMethods.ShouldBe(
        [
            new SignInMethodDto("Email", "Linked", email),
            new SignInMethodDto("Google", "NotLinked", null),
            new SignInMethodDto("Phone", "ComingSoon", null),
        ]);
    }

    [Fact]
    public async Task ABadDisplayNameIsABadRequest()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var tokens = await SignInAsync(api, client, NewAddress());

        using var blank = await SendAsync(client, HttpMethod.Put, "/api/v1/profile", tokens.AccessToken, new UpdateProfileRequest("   "));
        using var tooLong = await SendAsync(client, HttpMethod.Put, "/api/v1/profile", tokens.AccessToken, new UpdateProfileRequest(new string('a', 51)));

        blank.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheProfileNeedsAValidAccessTokenIssuedByJTAuth()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var tokens = await SignInAsync(api, client, NewAddress());
        var otherKeyFile = Path.Combine(Path.GetDirectoryName(keyFile)!, "other", "signing-key.json");
        using var impostor = new JTAuthApi(database.ConnectionString, otherKeyFile);
        using var impostorClient = impostor.CreateClient();
        var forged = await SignInAsync(impostor, impostorClient, NewAddress());

        using var anonymous = await SendAsync(client, HttpMethod.Get, "/api/v1/profile", null);
        using var tampered = await SendAsync(client, HttpMethod.Get, "/api/v1/profile", tokens.AccessToken[..^3] + "AAA");
        using var refreshTokenInsteadOfAccessToken = await SendAsync(client, HttpMethod.Get, "/api/v1/profile", tokens.RefreshToken);
        using var otherIssuersKey = await SendAsync(client, HttpMethod.Get, "/api/v1/profile", forged.AccessToken);
        using var genuine = await SendAsync(client, HttpMethod.Get, "/api/v1/profile", tokens.AccessToken);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        tampered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refreshTokenInsteadOfAccessToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        otherIssuersKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "signed by a different key than the one JTAuth publishes");
        genuine.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ATokenForADisabledAppNoLongerOpensTheProfile()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var clientId = "tmp" + Guid.NewGuid().ToString("N")[..10];
        await database.ExecuteAsync("INSERT dbo.Client (ClientId, Name, Audience, IsEnabled) VALUES (@clientId, N'Temporary', @clientId, 1);", new { clientId });
        var tokens = await SignInAsync(api, client, NewAddress(), clientId);
        (await SendAsync(client, HttpMethod.Get, "/api/v1/profile", tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await database.ExecuteAsync("UPDATE dbo.Client SET IsEnabled = 0 WHERE ClientId = @clientId;", new { clientId });

        (await SendAsync(client, HttpMethod.Get, "/api/v1/profile", tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(client, tokens.RefreshToken, clientId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LoggingOutEndsTheSessionAndAnswersTheSameForAnyToken()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var tokens = await SignInAsync(api, client, NewAddress());

        using var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(tokens.RefreshToken), Cancel);
        using var again = await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(tokens.RefreshToken), Cancel);
        using var unknown = await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest("not-a-token-that-exists"), Cancel);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RefreshAsync(client, tokens.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARefreshRequestForAnUnknownClientOrWithNoTokenIsRefused()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var tokens = await SignInAsync(api, client, NewAddress());

        (await RefreshAsync(client, tokens.RefreshToken, "nosuchapp")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RefreshAsync(client, "", "citybars")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RefreshAsync(client, "unknown-token", "citybars")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangingTheSignInEmailNeedsACodeSentToTheNewAddress()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var oldAddress = NewAddress();
        var newAddress = NewAddress();
        var tokens = await SignInAsync(api, client, oldAddress);

        using var request = await SendAsync(client, HttpMethod.Post, "/api/v1/profile/email/request-code", tokens.AccessToken, new RequestEmailChangeRequest(newAddress));
        var sent = api.Emails.Sent[^1];
        using var wrong = await SendAsync(client, HttpMethod.Post, "/api/v1/profile/email/verify", tokens.AccessToken,
            new ConfirmEmailChangeRequest(newAddress, sent.Code == "000000" ? "111111" : "000000"));
        using var confirm = await SendAsync(client, HttpMethod.Post, "/api/v1/profile/email/verify", tokens.AccessToken, new ConfirmEmailChangeRequest(newAddress, sent.Code));

        request.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        sent.To.ShouldBe(newAddress);
        sent.AppName.ShouldBe("CityBars");
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await confirm.Content.ReadFromJsonAsync<ProfileDto>(Cancel))!.SignInMethods[0].Address.ShouldBe(newAddress);

        var withNew = await SignInAsync(api, client, newAddress);
        var withOld = await SignInAsync(api, client, oldAddress);
        withNew.IsNewUser.ShouldBeFalse("the new address signs in to the same account");
        new JsonWebToken(withNew.AccessToken).Subject.ShouldBe(new JsonWebToken(tokens.AccessToken).Subject);
        withOld.IsNewUser.ShouldBeTrue("nobody owns the old address any more");
    }

    [Fact]
    public async Task AskingToTakeAnAddressAnotherAccountHasAnswersTheSameButSendsNothing()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var mine = await SignInAsync(api, client, NewAddress());
        var takenAddress = NewAddress();
        await SignInAsync(api, client, takenAddress);
        var sentBefore = api.Emails.Sent.Count;

        using var taken = await SendAsync(client, HttpMethod.Post, "/api/v1/profile/email/request-code", mine.AccessToken, new RequestEmailChangeRequest(takenAddress));
        using var free = await SendAsync(client, HttpMethod.Post, "/api/v1/profile/email/request-code", mine.AccessToken, new RequestEmailChangeRequest(NewAddress()));

        taken.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        free.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await taken.Content.ReadAsStringAsync(Cancel)).ShouldBe(await free.Content.ReadAsStringAsync(Cancel));
        api.Emails.Sent.Count.ShouldBe(sentBefore + 1, "a code goes only to the free address");
    }

    [Fact]
    public async Task TheProfileEmailEndpointsNeedASignedInPerson()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();

        (await SendAsync(client, HttpMethod.Post, "/api/v1/profile/email/request-code", null, new RequestEmailChangeRequest("a@example.com"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(client, HttpMethod.Post, "/api/v1/profile/email/verify", null, new ConfirmEmailChangeRequest("a@example.com", "123456"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(client, HttpMethod.Put, "/api/v1/profile", null, new UpdateProfileRequest("Pat"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
