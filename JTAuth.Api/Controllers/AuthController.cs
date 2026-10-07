using JTAuth.Application.SignIn;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace JTAuth.Api.Controllers;

/// <summary>Email-code sign-in. Signing up and signing in are the same flow.</summary>
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    ICommandHandler<RequestLoginCodeCommand, CodeRequestedDto> requestCode,
    ICommandHandler<VerifyLoginCodeCommand, AuthTokensDto> verifyCode) : ApiController
{
    /// <summary>Emails a one-time code. Answers 202 with the same body for every address, whether or not it has an account.</summary>
    [HttpPost("code/request")]
    public async Task<ActionResult<CodeRequestedDto>> RequestCode(RequestCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RespondAccepted(await requestCode.HandleAsync(new RequestLoginCodeCommand(request.ClientId, request.Email), cancellationToken));
    }

    /// <summary>Exchanges the code for an access token, creating the account on a first sign-in.</summary>
    [HttpPost("code/verify")]
    public async Task<ActionResult<AuthTokensDto>> VerifyCode(VerifyCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await verifyCode.HandleAsync(new VerifyLoginCodeCommand(request.ClientId, request.Email, request.Code), cancellationToken));
    }
}
