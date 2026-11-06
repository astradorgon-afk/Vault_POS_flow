using System.Reflection;
using FluentAssertions;
using Pos.Application.Identity;

namespace Pos.Architecture.Tests;

/// <summary>
/// The permission catalogue, held against the code that enforces it.
/// </summary>
/// <remarks>
/// <para>
/// A permission nobody checks is worse than a missing one. It is offered in the
/// role editor and in the override picker as though granting it did something,
/// so an administrator can spend authority on it and a reviewer can read a role
/// as narrower than it is. These tests make that state visible instead of
/// letting it accumulate.
/// </para>
/// <para>
/// Enforcement is looked for in the two layers that do it: the API's
/// <c>RequirePermission</c> attributes and the application's
/// <c>IAuthorizedMessage.RequiredPermission</c>. The catalogue and the role
/// bundles are excluded — naming a permission there is the declaration, not the
/// check.
/// </para>
/// </remarks>
public sealed class PermissionCatalogueTests
{
    /// <summary>The layers where a permission is actually checked.</summary>
    private static readonly string[] EnforcementLayers = ["Pos.Api", "Pos.Application"];

    /// <summary>
    /// The catalogue and the role bundles. Both name every permission, so
    /// counting them as enforcement would make this test pass unconditionally.
    /// </summary>
    private static readonly string[] DeclarationFiles = ["Permissions.cs", "Roles.cs"];

    /// <summary>
    /// Permissions in the catalogue that nothing currently checks, each with the
    /// reason it is still listed. This is duplicated on purpose: a permission
    /// that stops being enforced, or a new one that never starts, fails the
    /// build rather than joining the list quietly. An entry leaves this list by
    /// being enforced or by being deleted from the catalogue — it is a decision
    /// waiting to be made, not a permanent exemption.
    /// </summary>
    private static readonly string[] NotYetEnforced =
    [
        // The timelines and the ledger view gate on inventory.view instead.
        "inventory.movement.view",

        // Named by MovementTypeRule.PermissionCode, which nothing reads: the
        // ledger consults the rule for states and justification only.
        "inventory.receive",
        "inventory.reserve",

        // Refused draws are recorded by the infrastructure, but no path grants
        // a negative posting on the strength of holding this.
        "inventory.negative_stock",

        // Replenishment recommendations do not create requests yet.
        "transfer.auto_replenish",

        // The register never asks to open the drawer outside a sale.
        "cashdrawer.open_without_sale",

        // Cost, margin and valuation reports gate on report.view alone.
        "report.view.financial",

        // There is no report export to gate.
        "report.export",
    ];

    /// <summary>
    /// The permissions that let someone sign something off, release held stock,
    /// settle a discrepancy or widen the business, rather than merely carry out
    /// work. Each must be in <see cref="Permissions.Privileged"/>, because the
    /// administration safeguards only refuse to hand out what is marked there:
    /// a decision permission left unmarked is one an administrator can grant to
    /// an account they just opened.
    /// </summary>
    private static readonly string[] DecisionPermissions =
    [
        Permissions.Administration.AllLocations,
        Permissions.Administration.ManageLocations,
        Permissions.Administration.ManageRoles,
        Permissions.Administration.ManageSettings,
        Permissions.Administration.ManageUsers,
        Permissions.Administration.ViewAudit,
        Permissions.Catalog.ApproveChanges,
        Permissions.Inventory.ApproveAdjustment,
        Permissions.Inventory.ApproveCount,
        Permissions.Inventory.NegativeStock,
        Permissions.Inventory.RebuildBalances,
        Permissions.Inventory.RunExpiry,
        Permissions.Purchasing.Approve,
        Permissions.Purchasing.AuthorizeDirectToStore,
        Permissions.Purchasing.ResolveDiscrepancy,
        Permissions.Quarantine.Release,
        Permissions.Transfer.Approve,
        Permissions.Transfer.AutoReplenish,
        Permissions.Transfer.IssuePreApproval,
        Permissions.Transfer.Reconcile,
    ];

    [Fact]
    public void EveryPermission_IsEitherEnforcedOrListedAsNotYetEnforced()
    {
        Dictionary<string, string> mentions = EnforcementMentions();

        string[] unenforced = [.. Permissions.All
            .Select(p => p.Code)
            .Where(code => !mentions.ContainsKey(code))
            .Except(NotYetEnforced, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        unenforced.Should().BeEmpty(
            "a permission the role editor offers must be checked somewhere, or be listed in NotYetEnforced with the reason");
    }

    [Fact]
    public void NotYetEnforced_ListsNothingThatIsNowEnforced()
    {
        Dictionary<string, string> mentions = EnforcementMentions();

        string[] stale = [.. NotYetEnforced
            .Where(mentions.ContainsKey)
            .Order(StringComparer.Ordinal)];

        stale.Should().BeEmpty(
            "these permissions are enforced now and should leave the list, so it keeps reading as the real backlog");
    }

    [Fact]
    public void NotYetEnforced_NamesOnlyPermissionsThatExist()
    {
        string[] unknown = [.. NotYetEnforced
            .Where(code => !Permissions.IsDefined(code))
            .Order(StringComparer.Ordinal)];

        unknown.Should().BeEmpty("a deleted permission should leave this list with it");
    }

    [Fact]
    public void EveryPrivilegedCode_IsInTheCatalogue()
    {
        string[] unknown = [.. Permissions.Privileged
            .Where(code => !Permissions.IsDefined(code))
            .Order(StringComparer.Ordinal)];

        unknown.Should().BeEmpty("a typo here silently stops guarding the permission it meant to name");
    }

    [Fact]
    public void EveryDecisionPermission_IsPrivileged()
    {
        string[] unguarded = [.. DecisionPermissions
            .Where(code => !Permissions.Privileged.Contains(code))
            .Order(StringComparer.Ordinal)];

        unguarded.Should().BeEmpty(
            "the safeguards only refuse to hand out what Privileged names, so an unmarked decision permission is one anybody with user.manage can grant themselves through a new account");
    }

    [Fact]
    public void AuditorRole_IsGrantedOnlyPermissionsThatChangeNothing()
    {
        string[] mutating = [.. Roles.DefaultGrants[Roles.Auditor]
            .Where(code => Permissions.Find(code)?.IsReadOnly != true)
            .Order(StringComparer.Ordinal)];

        mutating.Should().BeEmpty("the Auditor role exists to read everything and change nothing");
    }

    /// <summary>
    /// Finds every permission the enforcement layers mention, by its code or by
    /// the constant that holds it, and says where.
    /// </summary>
    private static Dictionary<string, string> EnforcementMentions()
    {
        (string Code, string Symbol)[] catalogue = [.. CatalogueSymbols()];
        Dictionary<string, string> found = new(StringComparer.Ordinal);

        foreach (string file in EnforcementSources())
        {
            string text = File.ReadAllText(file);

            foreach ((string code, string symbol) in catalogue)
            {
                if (found.ContainsKey(code))
                {
                    continue;
                }

                if (text.Contains('"' + code + '"', StringComparison.Ordinal)
                    || text.Contains(symbol, StringComparison.Ordinal))
                {
                    found[code] = Path.GetFileName(file);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Gets each permission as its code and the <c>Group.Member</c> path that
    /// names it in code, read from the catalogue itself so a new module needs no
    /// edit here.
    /// </summary>
    private static IEnumerable<(string Code, string Symbol)> CatalogueSymbols()
    {
        foreach (Type module in typeof(Permissions).GetNestedTypes(BindingFlags.Public))
        {
            foreach (FieldInfo field in module.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field is { IsLiteral: true, IsInitOnly: false }
                    && field.GetRawConstantValue() is string code)
                {
                    yield return (code, module.Name + "." + field.Name);
                }
            }
        }
    }

    private static IEnumerable<string> EnforcementSources()
        => EnforcementLayers
            .Select(layer => Path.Combine(RepositoryRoot(), "src", layer))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => !DeclarationFiles.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VaultFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   "Could not find the repository root (the directory holding VaultFlow.slnx) above " + AppContext.BaseDirectory + ".");
    }
}
