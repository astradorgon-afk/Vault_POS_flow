using System.Security.Cryptography;

namespace Pos.Client.Storage;

/// <summary>
/// The register's own key pair, generated on first use and kept in the operating
/// system's secure credential store (SECURITY.md, enrolment).
/// </summary>
/// <remarks>
/// Head office records only the thumbprint of the public half at enrolment. The
/// private half never leaves this store, so a copied enrolment code redeemed on
/// another machine binds that machine's key, not this one's.
/// </remarks>
/// <param name="storage">The platform's secure credential store.</param>
public sealed class DeviceKeyStore(ISecureStorage storage)
{
    private const string KeyName = "vaultflow.device.signing.key.v1";

    /// <summary>Gets the SHA-256 thumbprint of the device's public key, in hex.</summary>
    /// <returns>The thumbprint presented at enrolment.</returns>
    public async Task<string> GetThumbprintAsync()
    {
        using ECDsa key = await LoadOrCreateAsync().ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }

    private async Task<ECDsa> LoadOrCreateAsync()
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            string? stored = await storage.GetAsync(KeyName).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
                return key;
            }

            byte[] created = key.ExportPkcs8PrivateKey();
            try
            {
                await storage.SetAsync(KeyName, Convert.ToBase64String(created)).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(created);
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
