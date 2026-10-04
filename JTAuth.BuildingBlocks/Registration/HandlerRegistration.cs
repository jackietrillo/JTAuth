using System.Reflection;
using JTAuth.BuildingBlocks.Decorators;
using JTAuth.BuildingBlocks.Handlers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JTAuth.BuildingBlocks.Registration;

/// <summary>
/// Convention-based handler registration (no mediator, no Scrutor). Finds every
/// <see cref="ICommandHandler{TCommand,TResult}"/> and <see cref="IQueryHandler{TQuery,TResult}"/>
/// in an assembly, registers it against its interface and wraps it in the decorators, outermost first:
/// Logging → Timing → ExceptionMapping → Validation → handler.
/// </summary>
public static class HandlerRegistration
{
    // Innermost first: each decorator wraps the result of the previous one.
    private static readonly Type[] CommandDecorators =
    [
        typeof(ValidationCommandDecorator<,>),
        typeof(ExceptionMappingCommandDecorator<,>),
        typeof(TimingCommandDecorator<,>),
        typeof(LoggingCommandDecorator<,>),
    ];

    private static readonly Type[] QueryDecorators =
    [
        typeof(ValidationQueryDecorator<,>),
        typeof(ExceptionMappingQueryDecorator<,>),
        typeof(TimingQueryDecorator<,>),
        typeof(LoggingQueryDecorator<,>),
    ];

    public static IServiceCollection AddHandlers(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return services.AddHandlers(assembly.GetTypes());
    }

    internal static IServiceCollection AddHandlers(this IServiceCollection services, IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        var concreteTypes = types
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .ToList();

        RegisterValidators(services, concreteTypes);

        var handlersByInterface = concreteTypes
            .SelectMany(type => type.GetInterfaces()
                .Where(IsHandlerInterface)
                .Select(handlerInterface => (Interface: handlerInterface, Implementation: type)))
            .GroupBy(pair => pair.Interface, pair => pair.Implementation);

        foreach (var group in handlersByInterface)
        {
            var handlerInterface = group.Key;
            var implementations = group.ToList();

            if (implementations.Count > 1)
            {
                throw DuplicateHandler(handlerInterface, implementations.Select(type => type.FullName));
            }

            if (services.FirstOrDefault(descriptor => descriptor.ServiceType == handlerInterface) is { } existing)
            {
                var existingName = existing.ImplementationType?.FullName ?? "an earlier registration";
                throw DuplicateHandler(handlerInterface, [existingName, implementations[0].FullName]);
            }

            Register(services, handlerInterface, implementations[0]);
        }

        return services;
    }

    /// <summary>
    /// Registers every FluentValidation validator found next to the handlers as <c>IValidator&lt;TRequest&gt;</c>,
    /// so the validation decorator runs it. Without this a validator class would exist but never run.
    /// </summary>
    private static void RegisterValidators(IServiceCollection services, List<Type> concreteTypes)
    {
        foreach (var type in concreteTypes)
        {
            foreach (var validatorInterface in type.GetInterfaces()
                         .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IValidator<>)))
            {
                services.AddScoped(validatorInterface, type);
            }
        }
    }

    private static void Register(IServiceCollection services, Type handlerInterface, Type implementation)
    {
        var genericArguments = handlerInterface.GetGenericArguments();
        var decorators = handlerInterface.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
            ? CommandDecorators
            : QueryDecorators;

        var createHandler = ActivatorUtilities.CreateFactory(implementation, Type.EmptyTypes);
        var createDecorators = decorators
            .Select(decorator => ActivatorUtilities.CreateFactory(decorator.MakeGenericType(genericArguments), [handlerInterface]))
            .ToArray();

        services.AddScoped(handlerInterface, provider =>
        {
            var handler = createHandler(provider, null);
            foreach (var createDecorator in createDecorators)
            {
                handler = createDecorator(provider, [handler]);
            }

            return handler;
        });
    }

    private static bool IsHandlerInterface(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
            || type.GetGenericTypeDefinition() == typeof(IQueryHandler<,>));

    private static InvalidOperationException DuplicateHandler(Type handlerInterface, IEnumerable<string?> implementations) =>
        new($"Request '{handlerInterface.GetGenericArguments()[0].FullName}' has more than one handler: "
            + string.Join(", ", implementations) + ". Each command or query must have exactly one handler.");
}
