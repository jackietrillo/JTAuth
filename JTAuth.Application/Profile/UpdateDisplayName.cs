using FluentValidation;
using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;
using JTAuth.Domain;

namespace JTAuth.Application.Profile;

/// <summary>Sets the name the person goes by. It appears in the <c>name</c> claim of the access tokens issued from then on.</summary>
public sealed record UpdateDisplayNameCommand(Guid UserId, string DisplayName) : ICommand<ProfileDto>;

public sealed class UpdateDisplayNameValidator : AbstractValidator<UpdateDisplayNameCommand>
{
    public UpdateDisplayNameValidator()
    {
        RuleFor(command => command.DisplayName)
            .Must(name => DisplayName.TryNormalize(name, out _))
            .WithMessage($"Enter a name of 1 to {DisplayName.MaxLength} characters on one line.");
    }
}

public sealed class UpdateDisplayNameHandler(IUserRepository users) : ICommandHandler<UpdateDisplayNameCommand, ProfileDto>
{
    public async Task<Result<ProfileDto>> HandleAsync(UpdateDisplayNameCommand command, CancellationToken cancellationToken)
    {
        if (!DisplayName.TryNormalize(command.DisplayName, out var name))
        {
            return Result.Failure<ProfileDto>(ResultError.Validation("invalid_display_name", "The display name is not valid."));
        }

        if (!await users.UpdateDisplayNameAsync(command.UserId, name, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<ProfileDto>(ProfileMapping.UnknownUser);
        }

        return await ProfileMapping.LoadAsync(users, command.UserId, cancellationToken).ConfigureAwait(false);
    }
}
