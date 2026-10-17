using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using JTAuth.Contracts;
using JTAuth.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace JTAuth.IntegrationTests;

/// <summary>An app's server asking for the emails of its own users, over HTTP against a real database.</summary>
[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ContactsEndToEndTests(SqlServerDatabase database) : IDisposable
{
    private const string Lookup = "/api/v1/contacts/lookup";
    private const string OtherClientId = "contacts-test-app";

    private readonly string keyFile = Path.Combine(Path.GetTempPath(), "jtauth-contacts-" + Guid.NewGuid().ToString("N"), "signing-key.json");

    public void Dispose()
    {
        if (Path.GetDirectoryName(keyFile) is { } folder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";

    /// <summary>Gives the app a service key (stored as its hash) and, for the second app, registers it first.</summary>
    private async Task<(string CityBarsKey, string OtherKey)> GiveKeysAsync()
    {
        var cityBarsKey = "cb-" + Guid.NewGuid().ToString("N");
        var otherKey = "ot-" + Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Client WHERE ClientId = @otherClientId)
                INSERT dbo.Client (ClientId, Name, Audience, IsEnabled) VALUES (@otherClientId, N'Contacts Test App', 'contacts-test-app', 1);
            UPDATE dbo.Client SET ServiceKeyHash = @cityBarsHash WHERE ClientId = 'citybars';
            UPDATE dbo.Client SET ServiceKeyHash = @otherHash WHERE ClientId = @otherClientId;
            """,
            new { otherClientId = OtherClientId, cityBarsHash = ServiceKeyRules.Hash(cityBarsKey), otherHash = ServiceKeyRules.Hash(otherKey) });
        return (cityBarsKey, otherKey);
    }

    private static async Task<Guid> SignInAsync(JTAuthApi api, HttpClient client, string email, string clientId)
    {
        (await client.PostAsJsonAsync("/api/v1/auth/code/request", new RequestCodeRequest(clientId, email), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = api.Emails.Sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).Code;
        var response = await client.PostAsJsonAsync("/api/v1/auth/code/verify", new VerifyCodeRequest(clientId, email, code), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokensDto>(Cancel))!;
        return Guid.Parse(new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken).Subject);
    }

    private static async Task<HttpResponseMessage> AskAsync(HttpClient client, string? key, params Guid[] ids)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Lookup) { Content = JsonContent.Create(new ContactLookupRequest(ids)) };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return await client.SendAsync(request, Cancel);
    }

    [Fact]
    public async Task AnAppGetsTheEmailsOfItsOwnPeopleAndNoOnesElse()
    {
        database.SkipIfUnavailable();
        var (cityBarsKey, otherKey) = await GiveKeysAsync();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var pat = NewAddress();
        var sam = NewAddress();
        var onlyOther = NewAddress();
        var patId = await SignInAsync(api, client, pat, "citybars");
        var samId = await SignInAsync(api, client, sam, "citybars");
        var otherId = await SignInAsync(api, client, onlyOther, OtherClientId);

        var asCityBars = await AskAsync(client, cityBarsKey, patId, samId, otherId, Guid.NewGuid());

        asCityBars.StatusCode.ShouldBe(HttpStatusCode.OK, await asCityBars.Content.ReadAsStringAsync(Cancel));
        var items = (await asCityBars.Content.ReadFromJsonAsync<ContactLookupDto>(Cancel))!.Items;
        items.Select(item => (item.UserId, item.Email)).ShouldBe([(patId, pat), (samId, sam)], ignoreOrder: true);

        var asOther = (await (await AskAsync(client, otherKey, patId, samId, otherId)).Content.ReadFromJsonAsync<ContactLookupDto>(Cancel))!.Items;
        asOther.Select(item => (item.UserId, item.Email)).ShouldBe([(otherId, onlyOther)]);
    }

    [Fact]
    public async Task ASignInToAnotherAppDoesNotMakeAPersonVisibleToThisOne()
    {
        database.SkipIfUnavailable();
        var (cityBarsKey, _) = await GiveKeysAsync();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();
        var address = NewAddress();
        var onlyOther = await SignInAsync(api, client, address, OtherClientId);

        var before = (await (await AskAsync(client, cityBarsKey, onlyOther)).Content.ReadFromJsonAsync<ContactLookupDto>(Cancel))!.Items;
        before.ShouldBeEmpty();

        var sameId = await SignInAsync(api, client, address, "citybars");
        sameId.ShouldBe(onlyOther);
        var after = (await (await AskAsync(client, cityBarsKey, onlyOther)).Content.ReadFromJsonAsync<ContactLookupDto>(Cancel))!.Items;
        after.Select(item => item.Email).ShouldBe([address]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-key")]
    public async Task AMissingOrWrongKeyIsRefusedAndAPersonsAccessTokenIsNotAKey(string? key)
    {
        database.SkipIfUnavailable();
        await GiveKeysAsync();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();

        (await AskAsync(client, key, Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var address = NewAddress();
        var personId = await SignInAsync(api, client, address, "citybars");
        var accessToken = await AccessTokenAsync(api, client, address);
        (await AskAsync(client, accessToken, personId)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a person's token is not an app's key");
    }

    [Fact]
    public async Task AnAppWithNoKeySetCannotAskAtAll()
    {
        database.SkipIfUnavailable();
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.ExecuteAsync("UPDATE dbo.Client SET ServiceKeyHash = NULL;");
        }

        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();

        (await AskAsync(client, "anything", Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARequestWithNoIdsOrTooManyIsRefusedAfterTheKeyChecksOut()
    {
        database.SkipIfUnavailable();
        var (cityBarsKey, _) = await GiveKeysAsync();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();

        (await AskAsync(client, cityBarsKey)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AskAsync(client, cityBarsKey, [.. Enumerable.Range(0, 201).Select(_ => Guid.NewGuid())])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheAnswerIsNeverCached()
    {
        database.SkipIfUnavailable();
        var (cityBarsKey, _) = await GiveKeysAsync();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var client = api.CreateClient();

        var response = await AskAsync(client, cityBarsKey, Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    private static async Task<string> AccessTokenAsync(JTAuthApi api, HttpClient client, string email)
    {
        (await client.PostAsJsonAsync("/api/v1/auth/code/request", new RequestCodeRequest("citybars", email), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = api.Emails.Sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).Code;
        var response = await client.PostAsJsonAsync("/api/v1/auth/code/verify", new VerifyCodeRequest("citybars", email, code), Cancel);
        return (await response.Content.ReadFromJsonAsync<AuthTokensDto>(Cancel))!.AccessToken;
    }
}