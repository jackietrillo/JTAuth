using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using JTAuth.Application;
using JTAuth.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace JTAuth.IntegrationTests;

/// <summary>The sign-in service started in memory against a real database, with the emailed codes captured.</summary>
public sealed class JTAuthApi : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string> settings;

    public JTAuthApi(string connectionString, string signingKeyFile, Dictionary<string, string>? overrides = null)
    {
        settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:JTAuthDb"] = connectionString,
            ["JTAuth:Issuer"] = "http://localhost",
            ["JTAuth:CodeSecret"] = "integration-test-secret",
            ["JTAuth:SigningKeyFile"] = signingKeyFile,
            ["JTAuth:CodeRequestsPerAddressPerMinute"] = "100",
            ["JTAuth:CodeRequestsPerAddressPerHour"] = "100",
            ["RateLimits:CodeRequestsPerMinutePerIp"] = "1000",
            ["RateLimits:CodeRequestsPerHourPerIp"] = "1000",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }
    }

    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }
}

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<(string To, string AppName, string Code)> sent = [];

    public IReadOnlyList<(string To, string AppName, string Code)> Sent
    {
        get
        {
            lock (sent)
            {
                return sent.ToList();
            }
        }
    }

    public Task SendLoginCodeAsync(string toAddress, string appName, string code, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        lock (sent)
        {
            sent.Add((toAddress, appName, code));
        }

        return Task.CompletedTask;
    }
}

[Collection(SqlServerTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class SignInEndToEndTests(SqlServerDatabase database) : IDisposable
{
    private readonly string keyFile = Path.Combine(Path.GetTempPath(), "jtauth-e2e-" + Guid.NewGuid().ToString("N"), "signing-key.json");

    public void Dispose()
    {
        if (Path.GetDirectoryName(keyFile) is { } folder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<string> ProblemWithoutTraceIdAsync(HttpResponseMessage response)
    {
        var problem = (await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(Cancel))!;
        problem.Remove("traceId");
        return string.Join("|", problem.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static async Task<HttpResponseMessage> RequestCodeAsync(HttpClient client, string email, string clientId = "citybars") =>
        await client.PostAsJsonAsync("/api/v1/auth/code/request", new RequestCodeRequest(clientId, email), Cancel);

    private static async Task<HttpResponseMessage> VerifyAsync(HttpClient client, string email, string code, string clientId = "citybars") =>
        await client.PostAsJsonAsync("/api/v1/auth/code/verify", new VerifyCodeRequest(clientId, email, code), Cancel);

    private static async Task<AuthTokensDto> SignInAsync(JTAuthApi api, HttpClient client, string email, string clientId = "citybars")
    {
        (await RequestCodeAsync(client, email, clientId)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = api.Emails.Sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).Code;
        var response = await VerifyAsync(client, email, code, clientId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<AuthTokensDto>(Cancel))!;
    }

    /// <summary>
    /// An app's own service, set up the way an app would set it up: stock JWT bearer authentication pointed at JTAuth's published
    /// keys (the issuer as authority, so the discovery document and then the key set are fetched over HTTP), one scheme per audience.
    /// </summary>
    private static async Task<WebApplication> StartAppAsync(JTAuthApi api)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        void Configure(JwtBearerOptions options, string audience)
        {
            options.Authority = "http://localhost";
            options.Audience = audience;
            options.RequireHttpsMetadata = false;
            options.MapInboundClaims = false;
            options.BackchannelHttpHandler = api.Server.CreateHandler();
        }

        builder.Services.AddAuthentication()
            .AddJwtBearer("citybars", options => Configure(options, "citybars"))
            .AddJwtBearer("menugenerator", options => Configure(options, "menugenerator"));
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/citybars/me", (ClaimsPrincipal user) => user.FindFirstValue("sub"))
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = "citybars" });
        app.MapGet("/menugenerator/me", (ClaimsPrincipal user) => user.FindFirstValue("sub"))
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = "menugenerator" });

        await app.StartAsync(Cancel);
        return app;
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Cancel);
        return response.StatusCode;
    }

    [Fact]
    public async Task AnAppValidatesAJTAuthTokenWithStockJwtBearerAgainstThePublishedKeysAndItsOwnAudienceOnly()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        await using var app = await StartAppAsync(api);
        using var appClient = app.GetTestClient();

        var tokens = await SignInAsync(api, jtauth, NewAddress());

        (await GetAsync(appClient, "/citybars/me", tokens.AccessToken)).ShouldBe(HttpStatusCode.OK);
        (await GetAsync(appClient, "/menugenerator/me", tokens.AccessToken)).ShouldBe(HttpStatusCode.Unauthorized, "the token is for citybars, not another audience");
        (await GetAsync(appClient, "/citybars/me", tokens.AccessToken[..^3] + "AAA")).ShouldBe(HttpStatusCode.Unauthorized, "a tampered token");
    }

    [Fact]
    public async Task AnAccountIsCreatedAtTheFirstSignInAndTheSameAccountIsUsedAtTheNext()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        await using var app = await StartAppAsync(api);
        using var appClient = app.GetTestClient();
        var email = NewAddress();

        var first = await SignInAsync(api, jtauth, email);
        var second = await SignInAsync(api, jtauth, email.ToUpperInvariant());

        first.IsNewUser.ShouldBeTrue();
        second.IsNewUser.ShouldBeFalse();
        first.AccessTokenExpiresUtc.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(14));
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/citybars/me");
        firstRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first.AccessToken);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/citybars/me");
        secondRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        var firstSub = await (await appClient.SendAsync(firstRequest, Cancel)).Content.ReadAsStringAsync(Cancel);
        var secondSub = await (await appClient.SendAsync(secondRequest, Cancel)).Content.ReadAsStringAsync(Cancel);
        Guid.Parse(firstSub).ShouldNotBe(Guid.Empty);
        secondSub.ShouldBe(firstSub);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserIdentity WHERE ProviderSubject = @email;", new { email = email.ToLowerInvariant() })).ShouldBe(1);
    }

    [Fact]
    public async Task EachSignInIsRecordedInUserClient()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        var email = NewAddress();

        await SignInAsync(api, jtauth, email);
        var afterFirst = (await database.QueryAsync<(DateTime First, DateTime Last)>(
            "SELECT uc.FirstSignInUtc, uc.LastSignInUtc FROM dbo.UserClient uc JOIN dbo.UserIdentity i ON i.UserId = uc.UserId WHERE i.ProviderSubject = @email AND uc.ClientId = 1;", new { email })).Single();
        await Task.Delay(50, Cancel);
        await SignInAsync(api, jtauth, email);
        var afterSecond = (await database.QueryAsync<(DateTime First, DateTime Last)>(
            "SELECT uc.FirstSignInUtc, uc.LastSignInUtc FROM dbo.UserClient uc JOIN dbo.UserIdentity i ON i.UserId = uc.UserId WHERE i.ProviderSubject = @email AND uc.ClientId = 1;", new { email })).Single();

        afterFirst.Last.ShouldBe(afterFirst.First);
        afterSecond.First.ShouldBe(afterFirst.First);
        afterSecond.Last.ShouldBeGreaterThan(afterFirst.Last);
    }

    [Fact]
    public async Task TheRequestAnswersIdenticallyForAKnownAndAnUnknownAddress()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        var known = NewAddress();
        await SignInAsync(api, jtauth, known);

        using var forKnown = await RequestCodeAsync(jtauth, known);
        using var forUnknown = await RequestCodeAsync(jtauth, NewAddress());

        forKnown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        forUnknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await forKnown.Content.ReadAsStringAsync(Cancel)).ShouldBe(await forUnknown.Content.ReadAsStringAsync(Cancel));
        forKnown.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task APublishedKeySetHasPublicKeysOnlyAndDiscoveryPointsAtIt()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();

        using var jwksResponse = await jtauth.GetAsync(new Uri("/.well-known/jwks.json", UriKind.Relative), Cancel);
        using var jwks = JsonDocument.Parse(await jwksResponse.Content.ReadAsStringAsync(Cancel));
        using var discoveryResponse = await jtauth.GetAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative), Cancel);
        using var discovery = JsonDocument.Parse(await discoveryResponse.Content.ReadAsStringAsync(Cancel));

        jwksResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        jwksResponse.Headers.CacheControl!.Public.ShouldBeTrue();
        jwksResponse.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        var key = jwks.RootElement.GetProperty("keys").EnumerateArray().Single();
        key.GetProperty("kty").GetString().ShouldBe("RSA");
        key.GetProperty("alg").GetString().ShouldBe("RS256");
        key.GetProperty("use").GetString().ShouldBe("sig");
        key.GetProperty("kid").GetString().ShouldNotBeNullOrWhiteSpace();
        key.EnumerateObject().Select(property => property.Name).Order().ShouldBe(["alg", "e", "kid", "kty", "n", "use"]);
        discovery.RootElement.GetProperty("issuer").GetString().ShouldBe("http://localhost");
        discovery.RootElement.GetProperty("jwks_uri").GetString().ShouldBe("http://localhost/.well-known/jwks.json");
        discoveryResponse.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task EmailNamesTheRequestingApp()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        var email = NewAddress();

        await RequestCodeAsync(jtauth, email);

        api.Emails.Sent.Single(message => message.To == email).AppName.ShouldBe("CityBars");
    }

    [Fact]
    public async Task AWrongCodeIsRefusedWithoutSayingWhy()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        var email = NewAddress();
        await RequestCodeAsync(jtauth, email);
        var code = api.Emails.Sent.Single(message => message.To == email).Code;

        using var wrong = await VerifyAsync(jtauth, email, code == "000000" ? "111111" : "000000");
        using var neverAsked = await VerifyAsync(jtauth, NewAddress(), "123456");

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        neverAsked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemWithoutTraceIdAsync(wrong)).ShouldBe(await ProblemWithoutTraceIdAsync(neverAsked), "same answer: nothing reveals whether the address exists");
    }

    [Fact]
    public async Task ACodeWorksOnlyOnceOverHttp()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        var email = NewAddress();
        await RequestCodeAsync(jtauth, email);
        var code = api.Emails.Sent.Single(message => message.To == email).Code;

        using var first = await VerifyAsync(jtauth, email, code);
        using var second = await VerifyAsync(jtauth, email, code);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUnknownOrDisabledClientIsRefused()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();
        var disabled = "off" + Guid.NewGuid().ToString("N")[..10];
        await database.ExecuteAsync("INSERT dbo.Client (ClientId, Name, Audience, IsEnabled) VALUES (@disabled, N'Off', 'off', 0);", new { disabled });

        using var unknownRequest = await RequestCodeAsync(jtauth, NewAddress(), "nosuchapp");
        using var disabledRequest = await RequestCodeAsync(jtauth, NewAddress(), disabled);
        using var unknownVerify = await VerifyAsync(jtauth, NewAddress(), "123456", "nosuchapp");
        using var disabledVerify = await VerifyAsync(jtauth, NewAddress(), "123456", disabled);

        unknownRequest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknownVerify.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        disabledRequest.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        disabledVerify.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        api.Emails.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-an-email", "citybars")]
    [InlineData("", "citybars")]
    [InlineData("pat@example.com", "")]
    public async Task AMalformedRequestIsABadRequest(string email, string clientId)
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile);
        using var jtauth = api.CreateClient();

        using var response = await RequestCodeAsync(jtauth, email, clientId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AskingForTwoCodesWithinAMinuteForOneAddressIsRefused()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile, new Dictionary<string, string>
        {
            ["JTAuth:CodeRequestsPerAddressPerMinute"] = "1",
            ["JTAuth:CodeRequestsPerAddressPerHour"] = "5",
        });
        using var jtauth = api.CreateClient();
        var email = NewAddress();

        using var first = await RequestCodeAsync(jtauth, email);
        using var second = await RequestCodeAsync(jtauth, email);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        api.Emails.Sent.Count(message => message.To == email).ShouldBe(1);
    }

    [Fact]
    public async Task AnAddressOfTheCallerIsLimitedInCodeRequestsPerMinute()
    {
        database.SkipIfUnavailable();
        using var api = new JTAuthApi(database.ConnectionString, keyFile, new Dictionary<string, string>
        {
            ["RateLimits:CodeRequestsPerMinutePerIp"] = "2",
        });
        using var jtauth = api.CreateClient();

        using var first = await RequestCodeAsync(jtauth, NewAddress());
        using var second = await RequestCodeAsync(jtauth, NewAddress());
        using var third = await RequestCodeAsync(jtauth, NewAddress());

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.ShouldNotBeNull();
        api.Emails.Sent.Count.ShouldBe(2);
    }
}
