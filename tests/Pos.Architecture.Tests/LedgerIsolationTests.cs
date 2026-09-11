using System.Reflection;
using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Inventory;

namespace Pos.Architecture.Tests;

/// <summary>
/// Enforces the prime directive structurally: nothing outside the ledger may
/// create a movement or write a balance.
/// </summary>
/// <remarks>
/// These assertions are the build-time half of the guarantee. The runtime half
/// is the EF interceptor and the database triggers. Together they mean that
/// making stock change without an explanation requires deliberately defeating
/// four separate mechanisms.
/// </remarks>
public sealed class LedgerIsolationTests
{
    private const string LedgerNamespace = "Pos.Infrastructure.Inventory";

    private static readonly Assembly[] AllAssemblies =
    [
        typeof(Pos.Domain.Common.Money).Assembly,
        typeof(Pos.Application.DependencyInjection).Assembly,
        typeof(Pos.Infrastructure.DependencyInjection).Assembly,
        typeof(Pos.Api.Common.ProblemDetailsMapping).Assembly,
    ];

    [Fact]
    public void InventoryMovement_HasNoPublicConstructor()
    {
        ConstructorInfo[] publicConstructors =
            typeof(InventoryMovement).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        publicConstructors.Should().BeEmpty(
            "movements are created only by InventoryMovementGroup.Create, which enforces the zero-sum invariant");
    }

    [Fact]
    public void InventoryMovement_ExposesNoPublicSetters()
    {
        IEnumerable<string> settable = typeof(InventoryMovement)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name);

        settable.Should().BeEmpty("the ledger is append-only and immutable");
    }

    [Fact]
    public void InventoryBalance_HasNoPublicSetters_AndNoQuantityAssignment()
    {
        IEnumerable<string> settable = typeof(InventoryBalance)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name);

        settable.Should().BeEmpty("a balance changes only by applying a movement");

        IEnumerable<string> mutators = typeof(InventoryBalance)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(decimal)))
            .Select(m => m.Name);

        mutators.Should().BeEquivalentTo(
            ["WouldGoNegative"],
            "no public method may accept a target quantity; only WouldGoNegative inspects one");
    }

    [Fact]
    public void OnlyTheLedgerConstructsMovementGroups()
    {
        // Application code asks IInventoryLedger to post; it does not assemble
        // and persist movements itself.
        IEnumerable<string> offenders = AllAssemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.Namespace is not null
                        && !t.Namespace.StartsWith(LedgerNamespace, StringComparison.Ordinal)
                        && !t.Namespace.StartsWith("Pos.Domain.Inventory", StringComparison.Ordinal))
            .Where(ReferencesMovementGroupFactory)
            .Select(t => t.FullName!);

        offenders.Should().BeEmpty(
            "only the ledger implementation may build and persist a movement group");
    }

    [Fact]
    public void EveryMovementTypePermission_ExistsInTheCatalogue()
    {
        // A rule that names a permission which does not exist would silently
        // authorize nothing, or worse, be quietly skipped.
        foreach (InventoryMovementType type in Enum.GetValues<InventoryMovementType>())
        {
            string permission = MovementTypeRules.For(type).PermissionCode;

            Permissions.IsDefined(permission).Should().BeTrue(
                $"movement type {type} requires permission {permission}, which must exist in the catalogue");
        }
    }

    [Fact]
    public void AuditorRole_GrantsOnlyReadOnlyPermissions()
    {
        IReadOnlyList<string> granted = Roles.DefaultGrants[Roles.Auditor];

        IEnumerable<string> mutating = granted
            .Select(code => Permissions.Find(code))
            .Where(p => p is { IsReadOnly: false })
            .Select(p => p!.Code);

        mutating.Should().BeEmpty("the Auditor role must be able to read everything and change nothing");
    }

    [Fact]
    public void NoApprovalPermission_IsOfflineCapable()
    {
        // Offline must never widen authority. Any permission that approves
        // something, or that belongs to central authority, stays server-only.
        IEnumerable<string> offenders = Permissions.OfflineCapable
            .Where(p => p.Code.Contains(".approve", StringComparison.Ordinal)
                        || p.Code is "product.create"
                            or "product.price.manage"
                            or "quarantine.release"
                            or "quarantine.reject"
                            or "user.manage"
                            or "role.manage"
                            or "transfer.approve"
                            or "inventory.negative_stock")
            .Select(p => p.Code);

        offenders.Should().BeEmpty(
            "a network outage must not grant authority that requires approval when online");
    }

    [Fact]
    public void EveryRoleGrant_NamesAKnownPermission()
    {
        foreach ((string role, IReadOnlyList<string> grants) in Roles.DefaultGrants)
        {
            foreach (string code in grants)
            {
                Permissions.IsDefined(code).Should().BeTrue($"role {role} grants unknown permission {code}");
            }
        }
    }

    private static bool ReferencesMovementGroupFactory(Type type)
    {
        // A type that takes a MovementGroupSpec and returns movements is building
        // the ledger's rows; that belongs to the ledger alone.
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.ReturnType == typeof(InventoryMovement)
                      || m.ReturnType == typeof(IReadOnlyList<InventoryMovement>)
                      || m.ReturnType == typeof(List<InventoryMovement>));
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
