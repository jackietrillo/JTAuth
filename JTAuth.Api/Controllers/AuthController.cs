using JTAuth.Application.SignIn;
using JTAuth.Application.Tokens;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace JTAuth.Api.Controllers;

/// <summary>Email-code sign-in, and keeping the session going with refresh tokens. Signing up and signing in are the same flow.</summary>
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    ICommandHandler<RequestLoginCodeCommand, CodeRequestedDto> requestCode,
    ICommandHandler<VerifyLoginCodeCommand, AuthTokensDto> verifyCode,
    ICommandHandler<RefreshTokensCommand, AuthTokensDto> refresh,
    ICommandHandler<LogoutCommand, Unit> logout) : ApiController
{
    /// <summary>Emails a one-time code. Answers 202 with the same body for every address, whether or not it has an account.</summary>
    [HttpPost("code/request")]
    public async Task<ActionResult<CodeRequestedDto>> RequestCode(RequestCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RespondAccepted(await requestCode.HandleAsync(new RequestLoginCodeCommand(request.ClientId, request.Email), cancellationToken));
    }

    /// <summary>Exchanges the code for an access token and a refresh token, creating the account on a first sign-in.</summary>
    [HttpPost("code/verify")]
    public async Task<ActionResult<AuthTokensDto>> VerifyCode(VerifyCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await verifyCode.HandleAsync(new VerifyLoginCodeCommand(request.ClientId, request.Email, request.Code), cancellationToken));
    }

    /// <summary>Swaps a refresh token for a new access token and a new refresh token. The presented refresh token stops working.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthTokensDto>> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await refresh.HandleAsync(new RefreshTokensCommand(request.ClientId, request.RefreshToken), cancellationToken));
    }

    /// <summary>Ends the session the refresh token belongs to. Answers 204 whether or not the token is known.</summary>
    [HttpPost("logout")]
    public async Task<ActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RespondNoContent(await logout.HandleAsync(new LogoutCommand(request.RefreshToken), cancellationToken));
    }
}
