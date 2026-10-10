using JTAuth.Application.Profile;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JTAuth.Api.Controllers;

/// <summary>The signed-in person's own details. Needs an access token issued by JTAuth for any registered app.</summary>
[Route("api/v1/profile")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProfileController(
    IQueryHandler<GetProfileQuery, ProfileDto> getProfile,
    ICommandHandler<UpdateDisplayNameCommand, ProfileDto> updateDisplayName,
    ICommandHandler<RequestEmailChangeCommand, CodeRequestedDto> requestEmailChange,
    ICommandHandler<ConfirmEmailChangeCommand, ProfileDto> confirmEmailChange) : ApiController
{
    [HttpGet]
    public async Task<ActionResult<ProfileDto>> Get(CancellationToken cancellationToken) =>
        Respond(await getProfile.HandleAsync(new GetProfileQuery(CurrentUserId), cancellationToken));

    /// <summary>Sets the display name. The new name is in the access tokens issued from the next refresh on.</summary>
    [HttpPut]
    public async Task<ActionResult<ProfileDto>> Update(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await updateDisplayName.HandleAsync(new UpdateDisplayNameCommand(CurrentUserId, request.DisplayName), cancellationToken));
    }

    /// <summary>Sends a code to the new address. Answers 202 with the same body whether or not the address is free.</summary>
    [HttpPost("email/request-code")]
    public async Task<ActionResult<CodeRequestedDto>> RequestEmailChange(RequestEmailChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RespondAccepted(await requestEmailChange.HandleAsync(new RequestEmailChangeCommand(CurrentUserId, CurrentAudience, request.NewEmail), cancellationToken));
    }

    /// <summary>Replaces the sign-in email with the new address once the code sent to it is accepted.</summary>
    [HttpPost("email/verify")]
    public async Task<ActionResult<ProfileDto>> ConfirmEmailChange(ConfirmEmailChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await confirmEmailChange.HandleAsync(new ConfirmEmailChangeCommand(CurrentUserId, request.NewEmail, request.Code), cancellationToken));
    }
}
