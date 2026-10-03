namespace JTAuth.BuildingBlocks.Handlers;

/// <summary>Marker for a request that changes state.</summary>
public interface ICommand<TResult>;

/// <summary>Marker for a request that only reads state.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
