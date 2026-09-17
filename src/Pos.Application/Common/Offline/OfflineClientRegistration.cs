using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Application.Common.Behaviours;
using Pos.Application.Common.Messaging;

namespace Pos.Application.Common.Offline;

/// <summary>
/// Composes the application layer for a POS device. It is the counterpart of
/// <see cref="DependencyInjection.AddApplication"/>, and the difference between
/// them is the whole point: the server registers every handler in the assembly,
/// the device registers only what <see cref="OfflineCommandCatalogue"/> allows.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour pipeline is identical to the server's, in the same order. An
/// offline sale must be validated, authorized and audited exactly as an online
/// one; only the ports behind the behaviours differ (a cached permission
/// snapshot rather than a live database, a device-scoped number rather than a
/// central counter).
/// </para>
/// <para>
/// No query handler is registered. Device reads come from the device database
/// through its own read services, not from the server-shaped query handlers,
/// whose repositories address tables a device does not carry.
/// </para>
/// </remarks>
public static class OfflineClientRegistration
{
    /// <summary>
    /// Adds the dispatcher, the behaviour pipeline and the whitelisted command
    /// handlers and their validators.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="catalogue">
    /// The whitelist. Defaults to <see cref="OfflineCommandCatalogue.Default"/>;
    /// tests pass their own.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddOfflineClientApplication(
        this IServiceCollection services,
        OfflineCommandCatalogue? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        OfflineCommandCatalogue whitelist = catalogue ?? OfflineCommandCatalogue.Default;

        services.TryAddSingleton(whitelist);
        services.TryAddScoped<IDispatcher, Dispatcher>();

        // Same behaviours, same order as the server. See DependencyInjection.AddApplication.
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(LoggingBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(ValidationBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(AuthorizationBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(NegativeStockAttemptBehaviour<,>));
        services.AddTransient(typeof(IPipelineBehaviour<,>), typeof(UnitOfWorkBehaviour<,>));

        Assembly assembly = typeof(OfflineClientRegistration).Assembly;
        Type[] candidates = [.. assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false })];

        foreach (Type type in candidates)
        {
            foreach (Type contract in ClosedInterfaces(type, typeof(ICommandHandler<,>)))
            {
                if (whitelist.IsRegistered(contract.GetGenericArguments()[0]))
                {
                    services.AddScoped(contract, type);
                }
            }

            // Only the whitelisted commands' validators are registered. A
            // validator for a command the device cannot run would be dead
            // weight, and resolving one can drag in a server-only dependency.
            foreach (Type contract in ClosedInterfaces(type, typeof(IValidator<>)))
            {
                if (whitelist.IsRegistered(contract.GetGenericArguments()[0]))
                {
                    services.AddScoped(contract, type);
                }
            }
        }

        return services;
    }

    private static IEnumerable<Type> ClosedInterfaces(Type type, Type openGenericInterface)
        => type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGenericInterface);
}
