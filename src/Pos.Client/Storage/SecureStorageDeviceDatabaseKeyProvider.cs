using System.Security.Cryptography;
using Pos.Infrastructure.Offline;

namespace Pos.Client.Storage;

/// <summary>Stores the SQLCipher key in the operating system's secure credential store.</summary>
public sealed class SecureStorageDeviceDatabaseKeyProvider : IDeviceDatabaseKeyProvider
{
    private const string KeyName = "vaultflow.device.database.key.v1";

    /// <inheritdoc />
    /// <remarks>
    /// A stored key that no longer decodes is never replaced: a new key would
    /// silently orphan the encrypted store, so the failure surfaces instead.
    /// </remarks>
    public async ValueTask<byte[]> GetDatabaseKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? existing = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return Convert.FromBase64String(existing);
        }

        byte[] created = RandomNumberGenerator.GetBytes(DeviceDatabaseInitializer.KeyLengthBytes);
        try
        {
            await SecureStorage.Default.SetAsync(KeyName, Convert.ToBase64String(created)).ConfigureAwait(false);
            return created;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(created);
            throw;
        }
    }
}
