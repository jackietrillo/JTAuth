using JTAuth.Application;
using JTAuth.Application.Profile;
using JTAuth.Application.SignIn;
using JTAuth.Application.Tokens;
using JTAuth.BuildingBlocks;
using JTAuth.Contracts;
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

    public Task<Client?> FindByAudienceAsync(string audience, CancellationToken cancellationToken) =>
        Task.FromResult(clients.FirstOrDefault(client => client.Audience == audience));
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
    private readonly Dictionary<(IdentityProvider, string), Guid> identities = [];
    private readonly Dictionary<Guid, User> users = [];

    public IReadOnlyCollection<User> All => users.Values;

    public Task<(User User, bool IsNew)> GetOrCreateByIdentityAsync(IdentityProvider provider, string providerSubject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (identities.TryGetValue((provider, providerSubject), out var existingId))
        {
            return Task.FromResult((users[existingId], false));
        }

        var created = new User(Guid.NewGuid(), null, now);
        users[created.Id] = created;
        identities[(provider, providerSubject)] = created.Id;
        return Task.FromResult((created, true));
    }

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(users.TryGetValue(userId, out var user) ? user : null);

    public Task<Guid?> FindOwnerAsync(IdentityProvider provider, string providerSubject, CancellationToken cancellationToken) =>
        Task.FromResult(identities.TryGetValue((provider, providerSubject), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserIdentity>>(identities.Where(pair => pair.Value == userId).Select(pair => new UserIdentity(pair.Key.Item1, pair.Key.Item2)).ToList());

    public Task<bool> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken cancellationToken)
    {
        if (!users.TryGetValue(userId, out var user))
        {
            return Task.FromResult(false);
        }

        users[userId] = user with { DisplayName = displayName };
        return Task.FromResult(true);
    }

    public Task<EmailChangeResult> ChangeEmailAsync(Guid userId, string newEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!users.ContainsKey(userId))
        {
            return Task.FromResult(EmailChangeResult.NoSuchUser);
        }

        if (identities.TryGetValue((IdentityProvider.Email, newEmail), out var owner) && owner != userId)
        {
            return Task.FromResult(EmailChangeResult.AddressTaken);
        }

        foreach (var key in identities.Where(pair => pair.Value == userId && pair.Key.Item1 == IdentityProvider.Email).Select(pair => pair.Key).ToList())
        {
            identities.Remove(key);
        }

        identities[(IdentityProvider.Email, newEmail)] = userId;
        return Task.FromResult(EmailChangeResult.Changed);
    }

    /// <summary>Gives a person a Google identity, to test accounts that have both ways of signing in.</summary>
    public void LinkGoogle(Guid userId, string subject) => identities[(IdentityProvider.Google, subject)] = userId;

    /// <summary>Removes the person's email identity, as a Google-only account would be.</summary>
    public void UnlinkEmail(Guid userId)
    {
        foreach (var key in identities.Where(pair => pair.Value == userId && pair.Key.Item1 == IdentityProvider.Email).Select(pair => pair.Key).ToList())
        {
            identities.Remove(key);
        }
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

/// <summary>Follows the same rules as the SQL repository: a token is single use, and a chain can be ended as a whole.</summary>
internal sealed class InMemoryRefreshTokens : IRefreshTokenRepository
{
    private readonly List<RefreshToken> tokens = [];

    public IReadOnlyList<RefreshToken> All => tokens;

    public Task AddAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        tokens.Add(token with { Id = tokens.Count + 1 });
        return Task.CompletedTask;
    }

    public Task<RefreshToken?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(tokens.FirstOrDefault(token => token.TokenHash.AsSpan().SequenceEqual(tokenHash)));

    public Task<bool> RotateAsync(long tokenId, RefreshToken replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var index = tokens.FindIndex(token => token.Id == tokenId);
        if (tokens[index].RevokedUtc is not null || tokens[index].ExpiresUtc <= now)
        {
            return Task.FromResult(false);
        }

        tokens[index] = tokens[index] with { RevokedUtc = now };
        tokens.Add(replacement with { Id = tokens.Count + 1 });
        return Task.FromResult(true);
    }

    public Task RevokeChainAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].FamilyId == familyId && tokens[index].RevokedUtc is null)
            {
                tokens[index] = tokens[index] with { RevokedUtc = now };
            }
        }

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

/// <summary>The sign-in, refresh and profile handlers wired to in-memory stand-ins, with a clock the test controls.</summary>
internal sealed class SignInWorld
{
    public SignInWorld()
    {
        Clock = new TestClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        Request = new RequestLoginCodeHandler(Clients, Codes, Email, Settings, Clock);
        Verify = new VerifyLoginCodeHandler(Clients, Codes, Users, UserClients, RefreshTokens, Tokens, Settings, Clock);
        Refresh = new RefreshTokensHandler(Clients, RefreshTokens, Users, Tokens, Clock);
        Logout = new LogoutHandler(RefreshTokens, Clock);
        GetProfile = new GetProfileHandler(Users);
        UpdateDisplayName = new UpdateDisplayNameHandler(Users);
        RequestEmailChange = new RequestEmailChangeHandler(Clients, Users, Codes, Email, Settings, Clock);
        ConfirmEmailChange = new ConfirmEmailChangeHandler(Users, Codes, Settings, Clock);
    }

    public TestClock Clock { get; }

    public JTAuthSettings Settings { get; } = new() { Issuer = "https://auth.test", CodeSecret = "test-secret" };

    public InMemoryClients Clients { get; } = new();

    public InMemoryLoginCodes Codes { get; } = new();

    public InMemoryUsers Users { get; } = new();

    public RecordingUserClients UserClients { get; } = new();

    public InMemoryRefreshTokens RefreshTokens { get; } = new();

    public RecordingEmailSender Email { get; } = new();

    public RecordingTokenIssuer Tokens { get; } = new();

    public RequestLoginCodeHandler Request { get; }

    public VerifyLoginCodeHandler Verify { get; }

    public RefreshTokensHandler Refresh { get; }

    public LogoutHandler Logout { get; }

    public GetProfileHandler GetProfile { get; }

    public UpdateDisplayNameHandler UpdateDisplayName { get; }

    public RequestEmailChangeHandler RequestEmailChange { get; }

    public ConfirmEmailChangeHandler ConfirmEmailChange { get; }

    /// <summary>Asks for a code and returns the one that was emailed.</summary>
    public async Task<string> RequestCodeAsync(string address = "pat@example.com", string clientId = "citybars")
    {
        var result = await Request.HandleAsync(new RequestLoginCodeCommand(clientId, address), CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return Email.Last.Code;
    }

    public Task<Result<AuthTokensDto>> VerifyAsync(string code, string address = "pat@example.com", string clientId = "citybars") =>
        Verify.HandleAsync(new VerifyLoginCodeCommand(clientId, address, code), CancellationToken.None);

    /// <summary>A full sign-in with the emailed code. Moves the clock on two minutes afterwards so the next one is not rate limited.</summary>
    public async Task<AuthTokensDto> SignInAsync(string address = "pat@example.com", string clientId = "citybars")
    {
        var result = await VerifyAsync(await RequestCodeAsync(address, clientId), address, clientId);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        Clock.Advance(TimeSpan.FromMinutes(2));
        return result.Value!;
    }

    public Task<Result<AuthTokensDto>> RefreshAsync(string refreshToken, string clientId = "citybars") =>
        Refresh.HandleAsync(new RefreshTokensCommand(clientId, refreshToken), CancellationToken.None);

    /// <summary>The id of the person who has signed in with this address.</summary>
    public Guid UserIdOf(string address = "pat@example.com") =>
        Users.FindOwnerAsync(IdentityProvider.Email, address, CancellationToken.None).Result!.Value;

    /// <summary>A well-formed code that differs from the given one.</summary>
    public static string WrongCodeFor(string code) => code == "000000" ? "111111" : "000000";
}
