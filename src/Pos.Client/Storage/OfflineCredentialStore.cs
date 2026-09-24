using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pos.Client.Services;

namespace Pos.Client.Storage;

/// <summary>
/// Lets someone who has signed in at this register before sign in again while
/// head office cannot be reached.
/// </summary>
/// <remarks>
/// <para>
/// A successful online sign-in leaves a salted PBKDF2 verifier of the password
/// in the platform's secure store, never the password itself, together with
/// the user's identity and the permissions head office granted on that
/// session. An offline sign-in checks the typed password against the verifier.
/// </para>
/// <para>
/// This does not widen authority. What the user may actually do offline is
/// still decided by the permission snapshot head office issued with the store
/// data (OFFLINE_SYNC.md §4): it is trimmed to offline-capable permissions and
/// expires on its own. The verifier itself stops working after
/// <see cref="Validity"/>, so a user disabled centrally cannot keep signing in
/// on a register that never reconnects.
/// </para>
/// </remarks>
/// <param name="storage">The platform's secure credential store.</param>
public sealed class OfflineCredentialStore(ISecureStorage storage)
{
    /// <summary>How long after the last online sign-in the verifier still works.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    private const string KeyPrefix = "vaultflow.offline.signin.v1.";
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Remembers a user after head office has accepted their password here.</summary>
    /// <param name="userName">What they typed as their username.</param>
    /// <param name="password">The password head office accepted.</param>
    /// <param name="user">Who head office said they are.</param>
    /// <param name="signedInAtUtc">When they signed in.</param>
    /// <returns>A task that completes when the verifier is stored.</returns>
    public async Task RememberAsync(string userName, string password, HeadOfficeUser user, DateTimeOffset signedInAtUtc)
    {
        ArgumentNullException.ThrowIfNull(user);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(password, salt);

        StoredCredential stored = new(
            user.UserId,
            user.DisplayName,
            [.. user.Permissions],
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash),
            Iterations,
            signedInAtUtc);

        CryptographicOperations.ZeroMemory(hash);
        await storage.SetAsync(KeyFor(userName), JsonSerializer.Serialize(stored, Json)).ConfigureAwait(false);
    }

    /// <summary>Checks a password against the verifier left by the last online sign-in.</summary>
    /// <param name="userName">What they typed as their username.</param>
    /// <param name="password">What they typed as their password.</param>
    /// <param name="now">The device clock.</param>
    /// <returns>Who they are, or a reason the offline sign-in is refused.</returns>
    public async Task<OfflineSignInResult> VerifyAsync(string userName, string password, DateTimeOffset now)
    {
        string? json = await storage.GetAsync(KeyFor(userName)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return OfflineSignInResult.Refused(
                "Head office can’t be reached, and this account has not signed in at this register before. " +
                "Sign in once while the connection is up, then it will work offline too.");
        }

        StoredCredential? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredCredential>(json, Json);
        }
        catch (JsonException)
        {
            stored = null;
        }

        if (stored is null)
        {
            storage.Remove(KeyFor(userName));
            return OfflineSignInResult.Refused("Head office can’t be reached. Sign in again once the connection is back.");
        }

        if (now - stored.SignedInAtUtc > Validity || stored.SignedInAtUtc > now.AddMinutes(5))
        {
            return OfflineSignInResult.Refused(
                "Head office can’t be reached, and this account’s offline sign-in has expired. " +
                "Connect this register to head office to sign in.");
        }

        if (stored.Iterations != Iterations)
        {
            return OfflineSignInResult.Refused(
                "Head office can’t be reached, and this account must sign in online once after the register was updated.");
        }

        byte[] salt = Convert.FromBase64String(stored.Salt);
        byte[] expected = Convert.FromBase64String(stored.Hash);
        byte[] actual = Derive(password, salt);

        bool matches = CryptographicOperations.FixedTimeEquals(actual, expected);
        CryptographicOperations.ZeroMemory(actual);

        return matches
            ? OfflineSignInResult.Accepted(new HeadOfficeUser(stored.UserId, stored.DisplayName, stored.Permissions))
            : OfflineSignInResult.Refused("The username or password is incorrect.");
    }

    private static byte[] Derive(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

    // The key is a hash of the normalized username, so the store's key list
    // does not read as a staff directory.
    private static string KeyFor(string userName)
        => KeyPrefix + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userName.Trim().ToUpperInvariant())));

    private sealed record StoredCredential(
        Guid UserId,
        string DisplayName,
        List<string> Permissions,
        string Salt,
        string Hash,
        int Iterations,
        DateTimeOffset SignedInAtUtc);
}

/// <summary>The outcome of an offline sign-in.</summary>
/// <param name="User">Who signed in, when accepted.</param>
/// <param name="Reason">Why it was refused, otherwise.</param>
public sealed record OfflineSignInResult(HeadOfficeUser? User, string? Reason)
{
    /// <summary>An accepted offline sign-in.</summary>
    /// <param name="user">Who signed in.</param>
    /// <returns>The result.</returns>
    public static OfflineSignInResult Accepted(HeadOfficeUser user) => new(user, null);

    /// <summary>A refused offline sign-in.</summary>
    /// <param name="reason">Why, in words a cashier can act on.</param>
    /// <returns>The result.</returns>
    public static OfflineSignInResult Refused(string reason) => new(null, reason);
}
