namespace JTAuth.BuildingBlocks.Decorators;

/// <summary>Exposes the wrapped handler so the decorator chain can be inspected (diagnostics and tests).</summary>
public interface IHandlerDecorator
{
    object Inner { get; }
}

internal delegate Task<Result<TResult>> HandleDelegate<in TRequest, TResult>(TRequest request, CancellationToken cancellationToken);
