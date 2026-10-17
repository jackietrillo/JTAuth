using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;
using Microsoft.Extensions.Logging;

namespace JTAuth.Application.Contacts;

/// <summary>
/// An app's server asks for the sign-in emails of some of its own users, so its staff can tell accounts apart. The caller is the app,
/// identified by its service key; it can only ever see people who have signed in to that app.
/// </summary>
public sealed record LookupContactsQuery(string? ServiceKey, IReadOnlyList<Guid>? UserIds) : IQuery<ContactLookupDto>;

public sealed partial class LookupContactsHandler(IClientRepository clients, IUserClientRepository userClients, ILogger<LookupContactsHandler> logger)
    : IQueryHandler<LookupContactsQuery, ContactLookupDto>
{
    public const int MaxUserIds = 200;

    public async Task<Result<ContactLookupDto>> HandleAsync(LookupContactsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The key is checked first, so a caller without one learns nothing, not even what a valid request looks like.
        if (ServiceKeyRules.Hash(query.ServiceKey) is not { } hash
            || await clients.FindByServiceKeyHashAsync(hash, cancellationToken).ConfigureAwait(false) is not { } client)
        {
            return Result.Failure<ContactLookupDto>(ResultError.Unauthorized("invalid_service_key", "The service key is missing or not recognised."));
        }

        if (!client.IsEnabled)
        {
            return Result.Failure<ContactLookupDto>(ResultError.Forbidden("client_disabled", "This app is disabled."));
        }

        var ids = query.UserIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        if (ids.Count == 0 || ids.Count > MaxUserIds)
        {
            return Result.Failure<ContactLookupDto>(ResultError.Validation("invalid_user_ids", $"Send between 1 and {MaxUserIds} user ids."));
        }

        var found = await userClients.FindEmailsAsync(client.Id, ids, cancellationToken).ConfigureAwait(false);

        // Who asked and how many came back, never the addresses themselves.
        LogLookup(client.ClientId, ids.Count, found.Count);
        return Result.Success(new ContactLookupDto([.. found.Select(person => new ContactLookupItemDto(person.UserId, person.Email))]));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Contact lookup by {ClientId}: {Asked} asked, {Returned} returned.")]
    private partial void LogLookup(string clientId, int asked, int returned);
}
