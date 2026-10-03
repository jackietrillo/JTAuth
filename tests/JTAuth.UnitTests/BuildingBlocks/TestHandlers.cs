using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using FluentValidation;

namespace JTAuth.UnitTests.BuildingBlocks;

public sealed record PingQuery(string Message) : IQuery<string>;

public sealed class PingQueryHandler : IQueryHandler<PingQuery, string>
{
    public Task<Result<string>> HandleAsync(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success($"pong: {query.Message}"));
}

public sealed class OtherPingQueryHandler : IQueryHandler<PingQuery, string>
{
    public Task<Result<string>> HandleAsync(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success("other"));
}

public sealed class PingQueryValidator : AbstractValidator<PingQuery>
{
    public PingQueryValidator() => RuleFor(query => query.Message).NotEmpty();
}

public sealed record SaveThingCommand(int Id) : ICommand<Unit>;

public sealed class SaveThingCommandHandler : ICommandHandler<SaveThingCommand, Unit>
{
    public Task<Result<Unit>> HandleAsync(SaveThingCommand command, CancellationToken cancellationToken) =>
        command.Id < 0
            ? throw new InvalidOperationException("Negative ids blow up.")
            : Task.FromResult(Result.Success());
}

public sealed class NotAHandler;
