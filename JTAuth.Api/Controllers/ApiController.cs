using JTAuth.BuildingBlocks;
using Microsoft.AspNetCore.Mvc;

namespace JTAuth.Api.Controllers;

/// <summary>Base class for controllers: maps a <see cref="Result{T}"/> to an HTTP response, with ProblemDetails for errors.</summary>
[ApiController]
public abstract class ApiController : ControllerBase
{
    protected ActionResult<T> Respond<T>(Result<T> result) => Respond(result, value => Ok(value));

    protected ActionResult<T> RespondAccepted<T>(Result<T> result) => Respond(result, value => Accepted(value));

    private ActionResult<T> Respond<T>(Result<T> result, Func<T?, ActionResult> success)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            return success(result.Value);
        }

        var error = result.Error!;
        switch (error.Type)
        {
            case ResultErrorType.Validation when error.ValidationErrors is not { Count: > 0 }:
                return Problem(error.Message, statusCode: StatusCodes.Status400BadRequest, title: "Validation failed");
            case ResultErrorType.Validation:
                foreach (var (field, messages) in error.ValidationErrors ?? new Dictionary<string, string[]>())
                {
                    foreach (var message in messages)
                    {
                        ModelState.AddModelError(field, message);
                    }
                }

                return ValidationProblem(ModelState);
            case ResultErrorType.NotFound:
                return Problem(error.Message, statusCode: StatusCodes.Status404NotFound, title: "Not found");
            case ResultErrorType.Unauthorized:
                return Problem(error.Message, statusCode: StatusCodes.Status401Unauthorized, title: "Not authorized");
            case ResultErrorType.Forbidden:
                return Problem(error.Message, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
            case ResultErrorType.Conflict:
                return Problem(error.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
            case ResultErrorType.TooManyRequests:
                return Problem(error.Message, statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests");
            default:
                return Problem(error.Message, statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected error");
        }
    }
}
