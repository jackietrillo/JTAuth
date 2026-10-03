using JTAuth.BuildingBlocks.Handlers;
using Microsoft.Extensions.Logging;

namespace JTAuth.BuildingBlocks.Decorators;

internal static partial class HandlerTiming
{
    public static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(500);

    public static async Task<Result<TResult>> RunAsync<TRequest, TResult>(
        ILogger logger, TimeProvider timeProvider, TRequest request, HandleDelegate<TRequest, TResult> next,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var elapsed = timeProvider.GetElapsedTime(started);
            if (elapsed >= SlowThreshold)
            {
                LogSlow(logger, typeof(TRequest).Name, elapsed.TotalMilliseconds);
            }
            else
            {
                LogElapsed(logger, typeof(TRequest).Name, elapsed.TotalMilliseconds);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{RequestName} took {ElapsedMs:0} ms")]
    private static partial void LogElapsed(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{RequestName} was slow: {ElapsedMs:0} ms")]
    private static partial void LogSlow(ILogger logger, string requestName, double elapsedMs);
}

public sealed class TimingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    TimeProvider timeProvider,
    ILogger<TimingCommandDecorator<TCommand, TResult>> logger)
    : ICommandHandler<TCommand, TResult>, IHandlerDecorator
    where TCommand : ICommand<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken) =>
        HandlerTiming.RunAsync<TCommand, TResult>(logger, timeProvider, command, inner.HandleAsync, cancellationToken);
}

public sealed class TimingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    TimeProvider timeProvider,
    ILogger<TimingQueryDecorator<TQuery, TResult>> logger)
    : IQueryHandler<TQuery, TResult>, IHandlerDecorator
    where TQuery : IQuery<TResult>
{
    public object Inner => inner;

    public Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken) =>
        HandlerTiming.RunAsync<TQuery, TResult>(logger, timeProvider, query, inner.HandleAsync, cancellationToken);
}
