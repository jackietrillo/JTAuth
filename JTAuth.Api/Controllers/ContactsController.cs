using JTAuth.Application.Contacts;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace JTAuth.Api.Controllers;

/// <summary>
/// Service to service: an app's server asks about its own users. Not for browsers. The caller is identified by its service key
/// (<c>Authorization: Bearer &lt;key&gt;</c>), not by a person's access token.
/// </summary>
[Route("api/v1/contacts")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ContactsController(IQueryHandler<LookupContactsQuery, ContactLookupDto> lookup) : ApiController
{
    /// <summary>The sign-in email of each of the app's users in the request. Anyone who has not signed in to the calling app is left out.</summary>
    [HttpPost("lookup")]
    public async Task<ActionResult<ContactLookupDto>> Lookup(ContactLookupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await lookup.HandleAsync(new LookupContactsQuery(ServiceKey(), request.UserIds), cancellationToken));
    }

    private string? ServiceKey()
    {
        var header = Request.Headers[HeaderNames.Authorization].ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}