using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// Signing in at a register while head office cannot be reached. Every test
/// here protects one of three conditions — the password head office last
/// accepted here, a person the store data still lists as active, and unexpired
/// offline authority at this store — and each must fail closed on its own.
/// </summary>
public sealed class DeviceOfflineSignInTests
{
    [Fact]
    public async Task APersonHeadOfficeSignedInHere_CanSignInOffline_WithTheirCachedAuthority()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        Result<OfflineSignInGrant> signedIn = await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password);

        signedIn.IsSuccess.Should().BeTrue(signedIn.IsFailure ? signedIn.Error.ToString() : string.Empty);
        signedIn.Value.UserId.Should().Be(host.CashierId);
        signedIn.Value.DisplayName.Should().Be("Maria Santos");
        signedIn.Value.Permissions.Should().BeEquivalentTo(Permissions.Sales.Create, Permissions.Sales.OpenShift);
        signedIn.Value.AuthorityExpiresAtUtc.Should().Be(TemporaryDeviceDatabase.Now.AddHours(72));
    }

    [Fact]
    public async Task TheUsernameIsMatchedWithoutRegardToCaseOrSpaces()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        Result<OfflineSignInGrant> signedIn = await host.SignIn.SignInAsync("  CASHIER ", OfflineRegisterHost.Password);

        signedIn.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task WhatWasTypedOnline_AndTheRealUsername_BothWorkOffline()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();

        // Head office accepts an e-mail address as the login name.
        await host.SignIn.RememberAsync(host.CashierId, "maria@store.example", "Maria Santos", OfflineRegisterHost.Password);

        (await host.SignIn.SignInAsync("maria@store.example", OfflineRegisterHost.Password)).IsSuccess.Should().BeTrue();
        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).IsSuccess
            .Should().BeTrue("the username comes from the store data head office sent");
    }

    [Fact]
    public async Task SomeoneWhoNeverSignedInHereWhileConnected_IsNotRemembered()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();

        Result<OfflineSignInGrant> signedIn = await host.SignIn.SignInAsync("cashier2", OfflineRegisterHost.Password);

        signedIn.IsFailure.Should().BeTrue("the store data alone is not a credential");
        signedIn.Error.Should().Be(OfflineSignInErrors.NotRemembered);
    }

    [Fact]
    public async Task AWrongPassword_IsRefused()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        Result<OfflineSignInGrant> signedIn = await host.SignIn.SignInAsync("cashier", "cash12345");

        signedIn.IsFailure.Should().BeTrue();
        signedIn.Error.Should().Be(OfflineSignInErrors.CredentialsInvalid);
    }

    [Fact]
    public async Task RepeatedFailures_LockTheAccountHere_EvenAgainstTheRightPassword_UntilTheLockoutPasses()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        Result<OfflineSignInGrant> last = default!;
        for (int attempt = 0; attempt < DeviceOfflineSignIn.MaxFailedAttempts; attempt++)
        {
            last = await host.SignIn.SignInAsync("cashier", "guess" + attempt);
        }

        last.Error.Should().Be(OfflineSignInErrors.LockedOut, "the fifth failure locks it");
        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).Error
            .Should().Be(OfflineSignInErrors.LockedOut, "the lock holds against the right password too");

        host.Database.Clock.UtcNow = TemporaryDeviceDatabase.Now + DeviceOfflineSignIn.Lockout + TimeSpan.FromSeconds(1);

        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task TheLockIsStored_SoRestartingTheAppDoesNotLiftIt()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        for (int attempt = 0; attempt < DeviceOfflineSignIn.MaxFailedAttempts; attempt++)
        {
            await host.SignIn.SignInAsync("cashier", "guess" + attempt);
        }

        DeviceOfflineSignIn afterRestart = new(
            host.Database.Initializer, host.Database.Clock, OfflineRegisterHost.TestIterations);

        (await afterRestart.SignInAsync("cashier", OfflineRegisterHost.Password)).Error
            .Should().Be(OfflineSignInErrors.LockedOut);
    }

    [Fact]
    public async Task ANewPasswordAcceptedByHeadOffice_ReplacesTheOldOne()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", "new-password-1");

        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).Error
            .Should().Be(OfflineSignInErrors.CredentialsInvalid);
        (await host.SignIn.SignInAsync("cashier", "new-password-1")).IsSuccess.Should().BeTrue();

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.OfflineCredentials.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SomeoneTheStoreDataNoLongerLists_CannotSignInOffline()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        // Disabled or moved to another store at head office: the next baseline
        // anyone downloads here no longer carries them.
        await host.LoadBaselineAsync([Permissions.Sales.Create], includeCashier: false);

        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).Error
            .Should().Be(OfflineSignInErrors.AccountUnavailable);
    }

    [Fact]
    public async Task AnInactiveAccount_CannotSignInOffline()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);
        await host.LoadBaselineAsync([Permissions.Sales.Create], cashierActive: false);

        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).Error
            .Should().Be(OfflineSignInErrors.AccountUnavailable);
    }

    [Fact]
    public async Task OnceTheCachedAuthorityExpires_OfflineSignInStops()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        host.Database.Clock.UtcNow = TemporaryDeviceDatabase.Now.AddHours(73);

        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).Error
            .Should().Be(OfflineSignInErrors.AuthorityExpired);
    }

    [Fact]
    public async Task SomeoneWithNothingToDoHereOffline_IsRefused()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync(cashierPermissions: []);
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).Error
            .Should().Be(OfflineSignInErrors.NoOfflineAuthority);
    }

    [Fact]
    public async Task TheRememberedCredential_OutlivesAStoreDataRefresh()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        // Someone else signs in while connected, which replaces the store data.
        await host.LoadBaselineAsync([Permissions.Sales.Create, Permissions.Sales.OpenShift]);

        (await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password)).IsSuccess
            .Should().BeTrue("the credential is the register's own record, not a feed-owned cache");
    }

    [Fact]
    public async Task TheRegisterStoresAVerifier_NeverThePassword()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        await using SqliteConnection connection = new(host.Database.EncryptedConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT salt, verifier, iterations FROM local_offline_credential";
        await using SqliteDataReader row = await read.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();

        byte[] salt = (byte[])row["salt"];
        byte[] verifier = (byte[])row["verifier"];
        salt.Should().HaveCount(16);
        verifier.Should().HaveCount(32);
        Encoding.UTF8.GetString(verifier).Should().NotContain(OfflineRegisterHost.Password);
        Convert.ToInt32(row["iterations"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(OfflineRegisterHost.TestIterations);
    }

    [Fact]
    public async Task TheSameUsernameRememberedForSomeoneNew_ReplacesTheOldHolder()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.SignIn.RememberAsync(host.OtherCashierId, "cashier", "Previous Holder", "old-password-1");
        await host.SignIn.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        Result<OfflineSignInGrant> signedIn = await host.SignIn.SignInAsync("cashier", OfflineRegisterHost.Password);

        signedIn.Value.UserId.Should().Be(host.CashierId);
        (await host.SignIn.SignInAsync("cashier", "old-password-1")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ADefaultVerifier_UsesTheFullWorkFactor()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        DeviceOfflineSignIn production = new(host.Database.Initializer, host.Database.Clock);

        await production.RememberAsync(host.CashierId, "cashier", "Maria Santos", OfflineRegisterHost.Password);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.OfflineCredentials.SingleAsync()).Iterations.Should().Be(DeviceOfflineSignIn.DefaultIterations);
    }
}
