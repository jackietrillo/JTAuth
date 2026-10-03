using JTAuth.BuildingBlocks.Handlers;
using Microsoft.Extensions.Logging;

namespace JTAuth.BuildingBlocks.Decorators;

internal static partial class HandlerLogging
{
    public static async Task<Result<TResult>> RunAsync<TRequest, TResult>(
        ILogger logger, TRequest request, HandleDelegate<TRequest, TResult> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        LogHandling(logger, requestName);

        var result = await next(request, cancellationToken).ConfigureAwait(false);

        if (result.Error is { } error)
        {
            LogFailed(logger, requestName, error.Type, error.Code);
        }
        else
        {
            LogHandled(logger, requestName);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Handling {RequestName}")]
    private static partial void LogHandling(ILogger logger, string requestName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handled {RequestName}")]
    private static partial void LogHandled(ILogger logger, string requestName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{RequestName} failed: {ErrorType} {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, string requestName, ResultErrorType errorType, string errorCode);
}

public sealed class LoggingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<LoggingCommandDecorator<TCommand, TResult>> logger)
    : ICommandHandler<TCommand, TResult>, IHandlerDecorator
    where TCommand : ICommand<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken) =>
        HandlerLogging.RunAsync<TCommand, TResult>(logger, command, inner.HandleAsync, cancellationToken);
}

public sealed class LoggingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ILogger<LoggingQueryDecorator<TQuery, TResult>> logger)
    : IQueryHandler<TQuery, TResult>, IHandlerDecorator
    where TQuery : IQuery<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken) =>
        HandlerLogging.RunAsync<TQuery, TResult>(logger, query, inner.HandleAsync, cancellationToken);
}
