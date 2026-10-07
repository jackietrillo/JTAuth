using JTAuth.BuildingBlocks;
using JTAuth.Domain;

namespace JTAuth.Application.SignIn;

/// <summary>The check every request that starts or finishes a sign-in makes first: the app must be registered and enabled.</summary>
internal static class ClientCheck
{
    public const string UnknownClientCode = "unknown_client";

    public const string DisabledClientCode = "client_disabled";

    public static async Task<(Client? Client, ResultError? Error)> FindUsableAsync(
        IClientRepository clients, string clientId, CancellationToken cancellationToken)
    {
        var client = await clients.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return (null, ResultError.Validation(UnknownClientCode, "The client is not registered.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["clientId"] = ["The client is not registered."] }));
        }

        return client.IsEnabled
            ? (client, null)
            : (null, ResultError.Forbidden(DisabledClientCode, "The client is disabled."));
    }
}
