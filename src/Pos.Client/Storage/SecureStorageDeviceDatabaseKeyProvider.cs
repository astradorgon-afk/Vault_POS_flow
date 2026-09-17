using System.Security.Cryptography;
using Pos.Infrastructure.Offline;

namespace Pos.Client.Storage;

/// <summary>Stores the SQLCipher key in the operating system's secure credential store.</summary>
public sealed class SecureStorageDeviceDatabaseKeyProvider : IDeviceDatabaseKeyProvider
{
    private const string KeyName = "vaultflow.device.database.key.v1";

    /// <inheritdoc />
    public async ValueTask<string> GetDatabaseKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? existing = await SecureStorage.Default.GetAsync(KeyName).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            string created = Convert.ToBase64String(bytes);
            await SecureStorage.Default.SetAsync(KeyName, created).ConfigureAwait(false);
            return created;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
