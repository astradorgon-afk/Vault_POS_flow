using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Common.Behaviours;
using Pos.Application.Common.Messaging;

namespace Pos.Application;

/// <summary>Registers the application layer with the dependency injection container.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the dispatcher, the behaviour pipeline and every handler and
    /// validator in this assembly.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Behaviour order matters and is explicit here: logging wraps everything so
    /// failures anywhere are recorded; validation runs before authorization so a
    /// malformed request is not evaluated for permissions; the unit of work is
    /// innermost so a transaction opens only once the request is known to be
    /// well-formed and authorized.
    /// </remarks>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IDispatcher, Dispatcher>();

        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(LoggingBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(ValidationBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(AuthorizationBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(NegativeStockAttemptBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(UnitOfWorkBehaviour<,>));

        Assembly assembly = typeof(DependencyInjection).Assembly;

        services.Scan(assembly, typeof(ICommandHandler<,>));
        services.Scan(assembly, typeof(IQueryHandler<,>));
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        return services;
    }

    /// <summary>
    /// Registers every closed implementation of an open generic interface found
    /// in an assembly. Keeps handler registration declaration-free without
    /// taking a dependency on a third-party scanning library.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="openGenericInterface">The open generic service type.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection Scan(
        this IServiceCollection services,
        Assembly assembly,
        Type openGenericInterface)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(openGenericInterface);

        foreach (Type type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (Type contract in type.GetInterfaces()
                         .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGenericInterface))
            {
                services.AddScoped(contract, type);
            }
        }

        return services;
    }
}
