using JTAuth.BuildingBlocks.Handlers;
using Microsoft.Extensions.Logging;

namespace JTAuth.BuildingBlocks.Decorators;

internal static partial class HandlerExceptionMapping
{
    public const string ErrorCode = "unexpected_error";

    public static async Task<Result<TResult>> RunAsync<TRequest, TResult>(
        ILogger logger, TRequest request, HandleDelegate<TRequest, TResult> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Unexpected exceptions become a Result by design.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogUnhandled(logger, exception, typeof(TRequest).Name);
            return Result.Failure<TResult>(ResultError.Unexpected(ErrorCode, "An unexpected error occurred."));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in {RequestName}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string requestName);
}

public sealed class ExceptionMappingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<ExceptionMappingCommandDecorator<TCommand, TResult>> logger)
    : ICommandHandler<TCommand, TResult>, IHandlerDecorator
    where TCommand : ICommand<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken) =>
        HandlerExceptionMapping.RunAsync<TCommand, TResult>(logger, command, inner.HandleAsync, cancellationToken);
}

public sealed class ExceptionMappingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ILogger<ExceptionMappingQueryDecorator<TQuery, TResult>> logger)
    : IQueryHandler<TQuery, TResult>, IHandlerDecorator
    where TQuery : IQuery<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken) =>
        HandlerExceptionMapping.RunAsync<TQuery, TResult>(logger, query, inner.HandleAsync, cancellationToken);
}
