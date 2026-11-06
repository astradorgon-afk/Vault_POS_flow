using System.Text.Json;
using Pos.Client.Services;

namespace Pos.Client.Storage;

/// <summary>
/// What the register keeps from its last conversation with head office so it
/// can keep trading when the next one does not happen: each product's sale
/// unit, and the last checkout context (business date, cash rounding, the open
/// shift).
/// </summary>
/// <remarks>
/// None of this is authority. Prices and permissions come from the encrypted
/// device store; these are the identifiers a sale line needs and the facts the
/// till shows, and head office re-derives and re-checks all of them when the
/// queued sale is replayed.
/// </remarks>
/// <param name="preferences">Where the checkout context is kept.</param>
/// <param name="directory">The app's private data directory.</param>
public sealed class OfflineRegisterCache(IPreferences preferences, string directory)
{
    private const string ContextPreference = "vaultflow.offline.checkout-context.v1";
    private const string ReferencesFile = "sale-references.v1.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Keeps the sale unit of every product head office sent.</summary>
    /// <param name="references">The references, keyed by product.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>A task that completes when they are written.</returns>
    public async Task SaveReferencesAsync(
        IReadOnlyDictionary<Guid, ProductSaleReference> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);

        string path = Path.Combine(directory, ReferencesFile);
        string temporary = path + ".tmp";
        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, references.Values.ToList(), Json, cancellationToken)
                .ConfigureAwait(false);
        }

        // Replace in one step, so a crash mid-write leaves the previous copy.
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Reads the sale units kept by the last online sign-in.</summary>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The references, keyed by product; empty if there are none.</returns>
    public async Task<IReadOnlyDictionary<Guid, ProductSaleReference>> LoadReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(directory, ReferencesFile);
        if (!File.Exists(path))
        {
            return new Dictionary<Guid, ProductSaleReference>();
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            List<ProductSaleReference>? stored = await JsonSerializer
                .DeserializeAsync<List<ProductSaleReference>>(stream, Json, cancellationToken)
                .ConfigureAwait(false);

            return stored?
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First())
                ?? new Dictionary<Guid, ProductSaleReference>();
        }
        catch (JsonException)
        {
            return new Dictionary<Guid, ProductSaleReference>();
        }
    }

    /// <summary>Keeps the checkout context head office just returned.</summary>
    /// <param name="deviceId">The register it belongs to.</param>
    /// <param name="context">The context.</param>
    /// <param name="fetchedAtUtc">When head office returned it.</param>
    public void SaveContext(Guid deviceId, RegisterCheckoutContext context, DateTimeOffset fetchedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        preferences.Set(
            ContextPreference,
            JsonSerializer.Serialize(new CachedCheckoutContext(deviceId, fetchedAtUtc, context), Json));
    }

    /// <summary>Reads the last checkout context head office returned for this register.</summary>
    /// <param name="deviceId">The register.</param>
    /// <returns>The context and when it was fetched, or null if there is none for this register.</returns>
    public CachedCheckoutContext? LoadContext(Guid deviceId)
    {
        string json = preferences.Get(ContextPreference, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            CachedCheckoutContext? cached = JsonSerializer.Deserialize<CachedCheckoutContext>(json, Json);
            return cached?.DeviceId == deviceId ? cached : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A checkout context as head office last returned it.</summary>
/// <param name="DeviceId">The register it belongs to.</param>
/// <param name="FetchedAtUtc">When head office returned it.</param>
/// <param name="Context">The context.</param>
public sealed record CachedCheckoutContext(Guid DeviceId, DateTimeOffset FetchedAtUtc, RegisterCheckoutContext Context);
