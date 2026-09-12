using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Infrastructure.Identity;

namespace Pos.Security.Tests;

/// <summary>
/// The permission matrix from the design, asserted against the running system
/// rather than against the document.
/// </summary>
[Collection("api")]
public sealed class AuthorizationTests(PosApiFactory factory)
{
    private static readonly LocationId MainWarehouse = LocationId.New();
    private static readonly LocationId StoreOne = LocationId.New();
    private static readonly LocationId StoreTwo = LocationId.New();

    [Fact]
    public async Task Cashier_CannotReachDeviceAdministration()
    {
        await factory.CreateUserAsync("authz-cashier", Roles.Cashier, [StoreOne]);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "authz-cashier");

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/devices", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Administrator_CanReachDeviceAdministration()
    {
        await factory.CreateUserAsync("authz-admin", Roles.Administrator, [MainWarehouse], ApprovalTier.Tier3);
        using HttpClient client = factory.CreateClient();

        string token = await SignInAsync(client, "authz-admin");

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/devices", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cashier_CannotApproveTransfers()
    {
        UserId cashier = await factory.CreateUserAsync("authz-cashier-transfer", Roles.Cashier, [StoreOne]);

        (await HasPermissionAsync(cashier, Permissions.Transfer.Approve, StoreOne)).Should().BeFalse();
        (await HasPermissionAsync(cashier, Permissions.Inventory.ApproveAdjustment, StoreOne)).Should().BeFalse();
        (await HasPermissionAsync(cashier, Permissions.Sales.Create, StoreOne)).Should().BeTrue();
    }

    [Fact]
    public async Task StoreManager_CannotActAtAnotherStore()
    {
        UserId manager = await factory.CreateUserAsync(
            "authz-manager", Roles.StoreManager, [StoreOne], ApprovalTier.Tier1);

        // The permission is genuinely held; the location is what refuses it.
        (await HasPermissionAsync(manager, Permissions.Inventory.Adjust, StoreOne)).Should().BeTrue();
        (await HasPermissionAsync(manager, Permissions.Inventory.Adjust, StoreTwo)).Should().BeFalse();
        (await HasPermissionAsync(manager, Permissions.Inventory.Adjust, MainWarehouse)).Should().BeFalse();
    }

    [Fact]
    public async Task StoreManager_CannotCreateProducts_OrChangePrices()
    {
        UserId manager = await factory.CreateUserAsync(
            "authz-manager-catalog", Roles.StoreManager, [StoreOne], ApprovalTier.Tier1);

        // The centralized Product Master rule: stores do not invent products or
        // reprice them, whatever else they are trusted with.
        (await HasPermissionAsync(manager, Permissions.Catalog.Create, StoreOne)).Should().BeFalse();
        (await HasPermissionAsync(manager, Permissions.Catalog.ManagePrices, StoreOne)).Should().BeFalse();
        (await HasPermissionAsync(manager, Permissions.Quarantine.Release, StoreOne)).Should().BeFalse();
    }

    [Fact]
    public async Task InventoryStaff_CannotApproveTheirOwnWork()
    {
        UserId staff = await factory.CreateUserAsync("authz-staff", Roles.InventoryStaff, [MainWarehouse]);

        (await HasPermissionAsync(staff, Permissions.Inventory.Adjust, MainWarehouse)).Should().BeTrue();
        (await HasPermissionAsync(staff, Permissions.Inventory.ApproveAdjustment, MainWarehouse)).Should().BeFalse();
        (await HasPermissionAsync(staff, Permissions.Transfer.Approve, MainWarehouse)).Should().BeFalse();
    }

    [Fact]
    public async Task Auditor_HoldsNoPermissionThatChangesAnything()
    {
        UserId auditor = await factory.CreateUserAsync(
            "authz-auditor", Roles.Auditor, [MainWarehouse, StoreOne, StoreTwo]);

        IReadOnlySet<string> held = await EffectivePermissionsAsync(auditor);

        held.Should().NotBeEmpty();

        IEnumerable<string> mutating = held.Where(code => Permissions.Find(code)?.IsReadOnly != true);

        mutating.Should().BeEmpty("the Auditor role exists to read everything and change nothing");
    }

    [Fact]
    public async Task Owner_MayActAtEveryLocation_IncludingOnesTheyAreNotAssignedTo()
    {
        UserId owner = await factory.CreateUserAsync(
            "authz-owner", Roles.Owner, [MainWarehouse], ApprovalTier.Unlimited);

        // The owner holds location.all, which is the single mechanism that
        // bypasses scoping. Nothing else in the system does.
        (await HasPermissionAsync(owner, Permissions.Inventory.Adjust, StoreTwo)).Should().BeTrue();
        (await HasPermissionAsync(owner, Permissions.Transfer.Approve, StoreTwo)).Should().BeTrue();
    }

    [Fact]
    public async Task DenyOverride_BeatsAGrantFromARole()
    {
        UserId manager = await factory.CreateUserAsync(
            "authz-deny", Roles.StoreManager, [StoreOne], ApprovalTier.Tier1);

        (await HasPermissionAsync(manager, Permissions.Sales.Void, StoreOne)).Should().BeTrue();

        await AddOverrideAsync(manager, Permissions.Sales.Void, PermissionEffect.Deny, "under investigation");

        // One capability suspended without unpicking anyone's roles.
        (await HasPermissionAsync(manager, Permissions.Sales.Void, StoreOne)).Should().BeFalse();
        (await HasPermissionAsync(manager, Permissions.Sales.Create, StoreOne)).Should().BeTrue();
    }

    [Fact]
    public async Task GrantOverride_AddsOneCapability_WithoutAPromotion()
    {
        UserId cashier = await factory.CreateUserAsync("authz-grant", Roles.Cashier, [StoreOne]);

        (await HasPermissionAsync(cashier, Permissions.Sales.Void, StoreOne)).Should().BeFalse();

        await AddOverrideAsync(cashier, Permissions.Sales.Void, PermissionEffect.Grant, "covering the late shift");

        (await HasPermissionAsync(cashier, Permissions.Sales.Void, StoreOne)).Should().BeTrue();

        // Still a cashier in every other respect.
        (await HasPermissionAsync(cashier, Permissions.Sales.Refund, StoreOne)).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredOverride_NoLongerApplies()
    {
        UserId cashier = await factory.CreateUserAsync("authz-expired", Roles.Cashier, [StoreOne]);

        await AddOverrideAsync(
            cashier,
            Permissions.Sales.Void,
            PermissionEffect.Grant,
            "one shift only",
            grantedAtUtc: DateTimeOffset.UtcNow.AddHours(-9),
            expiresAtUtc: DateTimeOffset.UtcNow.AddHours(-1));

        // Evaluated at read time rather than trusted to have been purged: a
        // nightly clean-up that fails must not quietly extend someone's authority.
        (await HasPermissionAsync(cashier, Permissions.Sales.Void, StoreOne)).Should().BeFalse();
    }

    [Fact]
    public async Task DisablingAUser_RemovesEveryPermission()
    {
        UserId manager = await factory.CreateUserAsync(
            "authz-disabled", Roles.StoreManager, [StoreOne], ApprovalTier.Tier1);

        (await HasPermissionAsync(manager, Permissions.Sales.Create, StoreOne)).Should().BeTrue();

        await factory.WithServiceAsync<Infrastructure.Persistence.PosDbContext>(async context =>
        {
            Guid id = manager.Value;
            AppUser user = await context.Users.AsTracking().FirstAsync(u => u.Id == id);

            user.IsActive = false;
            await context.SaveChangesAsync();
        });

        await BumpPolicyAsync("user disabled by a test");

        (await HasPermissionAsync(manager, Permissions.Sales.Create, StoreOne)).Should().BeFalse();
    }

    [Fact]
    public async Task EveryPermissionInTheCatalogue_IsSeededIntoTheDatabase()
    {
        await factory.WithServiceAsync<Infrastructure.Persistence.PosDbContext>(async context =>
        {
            List<string> stored = await context.Permissions.Select(p => p.Code).ToListAsync();

            // A permission an endpoint requires but the database has never heard
            // of would be an unsatisfiable check that reads like a working one.
            stored.Should().BeEquivalentTo(Permissions.All.Select(p => p.Code));
        });
    }

    [Fact]
    public async Task ApprovalTier_CapsWhatAnApproverMaySignOff()
    {
        UserId storeManager = await factory.CreateUserAsync(
            "authz-tier1", Roles.StoreManager, [StoreOne], ApprovalTier.Tier1);

        UserId inventoryManager = await factory.CreateUserAsync(
            "authz-tier2", Roles.MainInventoryManager, [MainWarehouse], ApprovalTier.Tier2);

        Result small = await RequireApprovalAsync(storeManager, 4_000m, StoreOne);
        Result large = await RequireApprovalAsync(storeManager, 40_000m, StoreOne);
        Result escalated = await RequireApprovalAsync(inventoryManager, 40_000m, MainWarehouse);

        small.IsSuccess.Should().BeTrue();

        // Same permission, same person, different consequence. A four-thousand
        // peso miscount and a forty-thousand peso write-off are not the same act.
        large.IsFailure.Should().BeTrue();
        large.Error.Code.Should().Be("approval.tier_exceeded");

        escalated.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SelfApproval_IsRefused()
    {
        UserId manager = await factory.CreateUserAsync(
            "authz-self", Roles.StoreManager, [StoreOne], ApprovalTier.Tier1);

        Result result = await RequireApprovalAsync(manager, 1_000m, StoreOne, documentCreator: manager);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("approval.self_approval_refused");
    }

    [Fact]
    public async Task ApproverOutsideTheirScope_IsRefused()
    {
        UserId manager = await factory.CreateUserAsync(
            "authz-scope", Roles.StoreManager, [StoreOne], ApprovalTier.Tier1);

        Result result = await RequireApprovalAsync(manager, 1_000m, StoreTwo);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("approval.approver_out_of_scope");
    }

    private async Task<bool> HasPermissionAsync(UserId userId, string permission, LocationId? location)
    {
        bool held = false;

        await factory.WithServiceAsync<IPermissionEvaluator>(async evaluator =>
            held = await evaluator.HasPermissionAsync(userId, permission, location, CancellationToken.None));

        return held;
    }

    private async Task<IReadOnlySet<string>> EffectivePermissionsAsync(UserId userId)
    {
        IReadOnlySet<string> permissions = new HashSet<string>(StringComparer.Ordinal);

        await factory.WithServiceAsync<IPermissionEvaluator>(async evaluator =>
            permissions = await evaluator.GetEffectivePermissionsAsync(userId, CancellationToken.None));

        return permissions;
    }

    private async Task<Result> RequireApprovalAsync(
        UserId approver,
        decimal value,
        LocationId location,
        UserId? documentCreator = null)
    {
        Result result = Result.Success();

        await factory.WithServiceAsync<IApprovalGate>(async gate =>
            result = await gate.RequireAsync(
                "inventory.adjust",
                value,
                approver,
                documentCreator ?? new UserId(Guid.CreateVersion7()),
                location,
                CancellationToken.None));

        return result;
    }

    private async Task AddOverrideAsync(
        UserId userId,
        string permission,
        PermissionEffect effect,
        string reason,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? grantedAtUtc = null)
    {
        await factory.WithServiceAsync<Infrastructure.Persistence.PosDbContext>(async context =>
        {
            Result<UserPermissionOverride> created = UserPermissionOverride.Create(
                userId,
                permission,
                effect,
                grantedAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(-5),
                userId,
                reason,
                expiresAtUtc);

            created.IsSuccess.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));

            context.UserPermissionOverrides.Add(created.Value);
            await context.SaveChangesAsync();
        });

        await BumpPolicyAsync("override changed by a test");
    }

    private async Task BumpPolicyAsync(string reason)
        => await factory.WithServiceAsync<IPolicyVersionProvider>(async provider =>
            await provider.BumpAsync(reason, CancellationToken.None));

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        return document.RootElement.GetProperty("accessToken").GetString()!;
    }
}
