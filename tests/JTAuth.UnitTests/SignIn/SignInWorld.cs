using JTAuth.Application;
using JTAuth.Application.SignIn;
using JTAuth.Domain;
using Shouldly;

namespace JTAuth.UnitTests.SignIn;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

internal sealed class InMemoryClients : IClientRepository
{
    private readonly List<Client> clients =
    [
        new(1, "citybars", "CityBars", "citybars", true),
        new(2, "otherapp", "Other App", "otherapp.example", true),
        new(3, "retired", "Retired App", "retired", false),
    ];

    public Task<Client?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(clients.SingleOrDefault(client => client.ClientId == clientId));
}

/// <summary>Follows the same rules as the SQL repository, so the handler tests exercise whole flows.</summary>
internal sealed class InMemoryLoginCodes : ILoginCodeRepository
{
    private readonly List<LoginCode> codes = [];

    public IReadOnlyList<LoginCode> All => codes;

    public Task<int> CountIssuedSinceAsync(IdentityProvider provider, string providerSubject, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(codes.Count(code => code.Provider == provider && code.ProviderSubject == providerSubject && code.CreatedUtc > since));

    public Task ReplaceAsync(LoginCode newCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var index = 0; index < codes.Count; index++)
        {
            var code = codes[index];
            if (code.Provider == newCode.Provider && code.ProviderSubject == newCode.ProviderSubject && code.ConsumedUtc is null)
            {
                codes[index] = code with { ConsumedUtc = now };
            }
        }

        codes.Add(newCode with { Id = codes.Count + 1 });
        return Task.CompletedTask;
    }

    public Task<LoginCode?> FindLatestAsync(IdentityProvider provider, string providerSubject, CancellationToken cancellationToken) =>
        Task.FromResult(codes.Where(code => code.Provider == provider && code.ProviderSubject == providerSubject).OrderByDescending(code => code.Id).FirstOrDefault());

    public Task<bool> TryClaimAttemptAsync(long codeId, int maxAttempts, CancellationToken cancellationToken)
    {
        var index = codes.FindIndex(code => code.Id == codeId);
        if (codes[index].ConsumedUtc is not null || codes[index].AttemptCount >= maxAttempts)
        {
            return Task.FromResult(false);
        }

        codes[index] = codes[index] with { AttemptCount = codes[index].AttemptCount + 1 };
        return Task.FromResult(true);
    }

    public Task<bool> TryConsumeAsync(long codeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var index = codes.FindIndex(code => code.Id == codeId);
        if (codes[index].ConsumedUtc is not null || codes[index].ExpiresUtc <= now)
        {
            return Task.FromResult(false);
        }

        codes[index] = codes[index] with { ConsumedUtc = now };
        return Task.FromResult(true);
    }
}

internal sealed class InMemoryUsers : IUserRepository
{
    private readonly Dictionary<(IdentityProvider, string), User> byIdentity = [];

    public IReadOnlyCollection<User> All => byIdentity.Values.Distinct().ToList();

    public Task<(User User, bool IsNew)> GetOrCreateByIdentityAsync(IdentityProvider provider, string providerSubject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (byIdentity.TryGetValue((provider, providerSubject), out var existing))
        {
            return Task.FromResult((existing, false));
        }

        var created = new User(Guid.NewGuid(), null, now);
        byIdentity[(provider, providerSubject)] = created;
        return Task.FromResult((created, true));
    }
}

internal sealed class RecordingUserClients : IUserClientRepository
{
    public List<(Guid UserId, int ClientId, DateTimeOffset At)> SignIns { get; } = [];

    public Task RecordSignInAsync(Guid userId, int clientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        SignIns.Add((userId, clientId, now));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public List<SentEmail> Sent { get; } = [];

    public SentEmail Last => Sent[^1];

    public Task SendLoginCodeAsync(string toAddress, string appName, string code, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        Sent.Add(new SentEmail(toAddress, appName, code, expiresUtc));
        return Task.CompletedTask;
    }
}

internal sealed record SentEmail(string To, string AppName, string Code, DateTimeOffset ExpiresUtc);

/// <summary>Returns a recognisable token so a test can see which person and app it was issued for.</summary>
internal sealed class RecordingTokenIssuer : IAccessTokenIssuer
{
    public List<(User User, Client Client)> Issued { get; } = [];

    public IssuedAccessToken Issue(User user, Client client, DateTimeOffset now)
    {
        Issued.Add((user, client));
        return new IssuedAccessToken($"token:{user.Id}:{client.Audience}", now + TokenRules.AccessTokenLifetime);
    }
}

/// <summary>The sign-in handlers wired to in-memory stand-ins, with a clock the test controls.</summary>
internal sealed class SignInWorld
{
    public SignInWorld()
    {
        Clock = new TestClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        Request = new RequestLoginCodeHandler(Clients, Codes, Email, Settings, Clock);
        Verify = new VerifyLoginCodeHandler(Clients, Codes, Users, UserClients, Tokens, Settings, Clock);
    }

    public TestClock Clock { get; }

    public JTAuthSettings Settings { get; } = new() { Issuer = "https://auth.test", CodeSecret = "test-secret" };

    public InMemoryClients Clients { get; } = new();

    public InMemoryLoginCodes Codes { get; } = new();

    public InMemoryUsers Users { get; } = new();

    public RecordingUserClients UserClients { get; } = new();

    public RecordingEmailSender Email { get; } = new();

    public RecordingTokenIssuer Tokens { get; } = new();

    public RequestLoginCodeHandler Request { get; }

    public VerifyLoginCodeHandler Verify { get; }

    /// <summary>Asks for a code and returns the one that was emailed.</summary>
    public async Task<string> RequestCodeAsync(string address = "pat@example.com", string clientId = "citybars")
    {
        var result = await Request.HandleAsync(new RequestLoginCodeCommand(clientId, address), CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return Email.Last.Code;
    }

    public Task<JTAuth.BuildingBlocks.Result<JTAuth.Contracts.AuthTokensDto>> VerifyAsync(string code, string address = "pat@example.com", string clientId = "citybars") =>
        Verify.HandleAsync(new VerifyLoginCodeCommand(clientId, address, code), CancellationToken.None);

    /// <summary>A well-formed code that differs from the given one.</summary>
    public static string WrongCodeFor(string code) => code == "000000" ? "111111" : "000000";
}
