using JTAuth.BuildingBlocks.Handlers;
using FluentValidation;
using FluentValidation.Results;

namespace JTAuth.BuildingBlocks.Decorators;

internal static class HandlerValidation
{
    public const string ErrorCode = "validation_failed";

    public static async Task<Result<TResult>> RunAsync<TRequest, TResult>(
        IEnumerable<IValidator<TRequest>> validators, TRequest request, HandleDelegate<TRequest, TResult> next,
        CancellationToken cancellationToken)
    {
        var failures = new List<ValidationFailure>();
        foreach (var validator in validators)
        {
            var validation = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            failures.AddRange(validation.Errors);
        }

        if (failures.Count == 0)
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var errors = failures
            .GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).ToArray(), StringComparer.Ordinal);

        return Result.Failure<TResult>(ResultError.Validation(ErrorCode, "One or more validation errors occurred.", errors));
    }
}

public sealed class ValidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand, TResult>, IHandlerDecorator
    where TCommand : ICommand<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken) =>
        HandlerValidation.RunAsync<TCommand, TResult>(validators, command, inner.HandleAsync, cancellationToken);
}

public sealed class ValidationQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IEnumerable<IValidator<TQuery>> validators)
    : IQueryHandler<TQuery, TResult>, IHandlerDecorator
    where TQuery : IQuery<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken) =>
        HandlerValidation.RunAsync<TQuery, TResult>(validators, query, inner.HandleAsync, cancellationToken);
}
