using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using JTAuth.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace JTAuth.IntegrationTests;

/// <summary>
/// Deleting an account over HTTP against a real database: it needs a recent sign-in, removes everything that says who the person is when no
/// other app has them, and otherwise only their place in the app that asked.
/// </summary>
[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class AccountDeletionEndToEndTests(SqlServerDatabase database) : IDisposable
{
    private readonly string keyFile = Path.Combine(Path.GetTempPath(), "jtauth-delete-" + Guid.NewGuid().ToString("N"), "signing-key.json");

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

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string accessToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, Cancel);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.ExecuteAsync(sql, parameters);
    }

    private Task<Guid> UserIdOfAsync(string email) =>
        ScalarAsync<Guid>("SELECT UserId FROM dbo.UserIdentity WHERE Provider = 'Email' AND ProviderSubject = @email", new { email });

    private async Task EnsureOtherAppAsync()
    {
        await ExecuteAsync(
            """
            SET IDENTITY_INSERT dbo.Client ON;
            IF NOT EXISTS (SELECT 1 FROM dbo.Client WHERE ClientId = 'otherapp')
                INSERT dbo.Client (Id, ClientId, Name, Audience, IsEnabled) VALUES (902, 'otherapp', N'Other App', 'otherapp.example', 1);
            SET IDENTITY_INSERT dbo.Client OFF;
            """);
    }

    [Fact]
    public async Task EveryAccessTokenSaysWhenThePersonSignedIn()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);

        var tokens = await SignInAsync(api, client, NewAddress());

        var claim = new JsonWebToken(tokens.AccessToken).GetClaim("auth_time");
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(claim.Value, System.Globalization.CultureInfo.InvariantCulture)).ShouldBeInRange(before, DateTimeOffset.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task ARefreshedTokenKeepsTheSignInTimeOfTheSession()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var first = await SignInAsync(api, client, email);
        await ExecuteAsync("UPDATE dbo.RefreshToken SET SignedInUtc = DATEADD(HOUR, -5, SYSUTCDATETIME()) WHERE UserId = @id", new { id = await UserIdOfAsync(email) });

        var refreshed = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("citybars", first.RefreshToken), Cancel)).Content.ReadFromJsonAsync<AuthTokensDto>(Cancel);

        var signedIn = DateTimeOffset.FromUnixTimeSeconds(long.Parse(new JsonWebToken(refreshed!.AccessToken).GetClaim("auth_time").Value, System.Globalization.CultureInfo.InvariantCulture));
        signedIn.ShouldBeLessThan(DateTimeOffset.UtcNow.AddHours(-4), "refreshing is not signing in again");
    }

    [Fact]
    public async Task APersonWhoJustSignedInCanDeleteTheirAccountAndNothingOfThemIsLeft()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var tokens = await SignInAsync(api, client, email);
        var userId = await UserIdOfAsync(email);
        await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("citybars", tokens.RefreshToken), Cancel);

        using var response = await SendAsync(client, HttpMethod.Delete, "/api/v1/profile", tokens.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        (await response.Content.ReadFromJsonAsync<AccountDeletedDto>(Cancel))!.AccountRemoved.ShouldBeTrue();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.[User] WHERE Id = @userId", new { userId })).ShouldBe(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE UserId = @userId", new { userId })).ShouldBe(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserClient WHERE UserId = @userId", new { userId })).ShouldBe(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.RefreshToken WHERE UserId = @userId", new { userId })).ShouldBe(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.LoginCode WHERE ProviderSubject = @email", new { email })).ShouldBe(0, "the address is not kept in the sign-in codes either");
    }

    [Fact]
    public async Task AfterTheDeleteTheSessionIsDeadAndTheAddressCanStartAgainAsANewPerson()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var tokens = await SignInAsync(api, client, email);
        var oldUserId = await UserIdOfAsync(email);
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/profile", tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("citybars", tokens.RefreshToken), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(client, HttpMethod.Get, "/api/v1/profile", tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/profile", tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var again = await SignInAsync(api, client, email);
        again.IsNewUser.ShouldBeTrue();
        (await UserIdOfAsync(email)).ShouldNotBe(oldUserId);
    }

    [Fact]
    public async Task ASignInOlderThanTenMinutesIsAskedToSignInAgainAndNothingIsDeleted()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var first = await SignInAsync(api, client, email);
        var userId = await UserIdOfAsync(email);
        await ExecuteAsync("UPDATE dbo.RefreshToken SET SignedInUtc = DATEADD(MINUTE, -11, SYSUTCDATETIME()) WHERE UserId = @userId", new { userId });
        var stale = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("citybars", first.RefreshToken), Cancel)).Content.ReadFromJsonAsync<AuthTokensDto>(Cancel);

        using var response = await SendAsync(client, HttpMethod.Delete, "/api/v1/profile", stale!.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Cancel)).ShouldContain("Sign in again");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.[User] WHERE Id = @userId", new { userId })).ShouldBe(1);
    }

    [Fact]
    public async Task SigningInAgainMakesTheDeleteAllowedAgain()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var first = await SignInAsync(api, client, email);
        await ExecuteAsync("UPDATE dbo.RefreshToken SET SignedInUtc = DATEADD(HOUR, -3, SYSUTCDATETIME()) WHERE UserId = @id", new { id = await UserIdOfAsync(email) });
        var stale = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("citybars", first.RefreshToken), Cancel)).Content.ReadFromJsonAsync<AuthTokensDto>(Cancel);
        (await SendAsync(client, HttpMethod.Delete, "/api/v1/profile", stale!.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var fresh = await SignInAsync(api, client, email);

        (await SendAsync(client, HttpMethod.Delete, "/api/v1/profile", fresh.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APersonWhoAlsoUsesAnotherAppOnlyLeavesTheAppThatAsked()
    {
        database.SkipIfUnavailable();
        await EnsureOtherAppAsync();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var email = NewAddress();
        var citybars = await SignInAsync(api, client, email);
        var other = await SignInAsync(api, client, email, "otherapp");
        var userId = await UserIdOfAsync(email);

        using var response = await SendAsync(client, HttpMethod.Delete, "/api/v1/profile", citybars.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<AccountDeletedDto>(Cancel))!.AccountRemoved.ShouldBeFalse();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.[User] WHERE Id = @userId", new { userId })).ShouldBe(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserClient WHERE UserId = @userId", new { userId })).ShouldBe(1, "only the other app's row is left");
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("citybars", citybars.RefreshToken), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("otherapp", other.RefreshToken), Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK, "the other app's session is untouched");
    }

    [Fact]
    public async Task AVisitorWithNoTokenIsRefused()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();

        (await client.DeleteAsync("/api/v1/profile", Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
