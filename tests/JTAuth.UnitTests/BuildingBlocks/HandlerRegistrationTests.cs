using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Decorators;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.BuildingBlocks.Registration;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace JTAuth.UnitTests.BuildingBlocks;

public sealed class HandlerRegistrationTests
{
    private static ServiceProvider BuildProvider(params Type[] types)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHandlers(types);
        return services.BuildServiceProvider();
    }

    private static List<Type> DecoratorChain(object handler)
    {
        var chain = new List<Type>();
        var current = handler;
        while (current is IHandlerDecorator decorator)
        {
            chain.Add(current.GetType().GetGenericTypeDefinition());
            current = decorator.Inner;
        }

        chain.Add(current.GetType());
        return chain;
    }

    [Fact]
    public async Task FindsEveryCommandAndQueryHandler()
    {
        await using var provider = BuildProvider(typeof(PingQueryHandler), typeof(SaveThingCommandHandler), typeof(NotAHandler));

        var query = provider.GetRequiredService<IQueryHandler<PingQuery, string>>();
        var command = provider.GetRequiredService<ICommandHandler<SaveThingCommand, Unit>>();

        (await query.HandleAsync(new PingQuery("hi"), CancellationToken.None)).Value.ShouldBe("pong: hi");
        (await command.HandleAsync(new SaveThingCommand(1), CancellationToken.None)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task WrapsQueryHandlersInDecoratorsInFixedOrder()
    {
        await using var provider = BuildProvider(typeof(PingQueryHandler));

        var handler = provider.GetRequiredService<IQueryHandler<PingQuery, string>>();

        DecoratorChain(handler).ShouldBe(
        [
            typeof(LoggingQueryDecorator<,>),
            typeof(TimingQueryDecorator<,>),
            typeof(ExceptionMappingQueryDecorator<,>),
            typeof(ValidationQueryDecorator<,>),
            typeof(PingQueryHandler),
        ]);
    }

    [Fact]
    public async Task WrapsCommandHandlersInDecoratorsInFixedOrder()
    {
        await using var provider = BuildProvider(typeof(SaveThingCommandHandler));

        var handler = provider.GetRequiredService<ICommandHandler<SaveThingCommand, Unit>>();

        DecoratorChain(handler).ShouldBe(
        [
            typeof(LoggingCommandDecorator<,>),
            typeof(TimingCommandDecorator<,>),
            typeof(ExceptionMappingCommandDecorator<,>),
            typeof(ValidationCommandDecorator<,>),
            typeof(SaveThingCommandHandler),
        ]);
    }

    [Fact]
    public void FailsClearlyOnDuplicateHandlerForTheSameRequest()
    {
        var services = new ServiceCollection();

        var exception = Should.Throw<InvalidOperationException>(
            () => services.AddHandlers([typeof(PingQueryHandler), typeof(OtherPingQueryHandler)]));

        exception.Message.ShouldContain(nameof(PingQuery));
        exception.Message.ShouldContain(nameof(PingQueryHandler));
        exception.Message.ShouldContain(nameof(OtherPingQueryHandler));
    }

    [Fact]
    public void FailsWhenARequestAlreadyHasAHandlerRegistered()
    {
        var services = new ServiceCollection();
        services.AddHandlers([typeof(PingQueryHandler)]);

        Should.Throw<InvalidOperationException>(() => services.AddHandlers([typeof(OtherPingQueryHandler)]))
            .Message.ShouldContain(nameof(PingQuery));
    }

    [Fact]
    public async Task RunsRegisteredValidatorAndSkipsHandlerOnFailure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IValidator<PingQuery>, PingQueryValidator>();
        services.AddHandlers([typeof(PingQueryHandler)]);
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IQueryHandler<PingQuery, string>>()
            .HandleAsync(new PingQuery(""), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(ResultErrorType.Validation);
        result.Error.ValidationErrors!.ShouldContainKey(nameof(PingQuery.Message));
    }

    [Fact]
    public async Task MapsUnexpectedExceptionsToAFailedResult()
    {
        await using var provider = BuildProvider(typeof(SaveThingCommandHandler));

        var result = await provider.GetRequiredService<ICommandHandler<SaveThingCommand, Unit>>()
            .HandleAsync(new SaveThingCommand(-1), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task LetsCancellationPropagate()
    {
        await using var provider = BuildProvider(typeof(CancellingQueryHandler));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => provider
            .GetRequiredService<IQueryHandler<PingQuery, string>>()
            .HandleAsync(new PingQuery("hi"), cancellation.Token));
    }

    public sealed class CancellingQueryHandler : IQueryHandler<PingQuery, string>
    {
        public Task<Result<string>> HandleAsync(PingQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success("not cancelled"));
        }
    }
}
