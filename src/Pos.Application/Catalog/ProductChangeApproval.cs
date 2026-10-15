using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>
/// A command that edits an existing product and is therefore subject to owner
/// approval.
/// </summary>
/// <remarks>
/// Creating a product is deliberately not marked: new items are captured at
/// goods receiving and in the catalogue without waiting on anybody. It is
/// changing an item that is already in use — its category, tax treatment, price
/// or barcodes — that the owner signs off.
/// </remarks>
public interface IProductChangeCommand
{
    /// <summary>Gets the product being changed.</summary>
    ProductId ProductId { get; }

    /// <summary>Gets a one-line description of the change, for the approval queue.</summary>
    string ChangeSummary { get; }
}

/// <summary>
/// Carries the reason a catalogue change was submitted with, for the duration of
/// one request.
/// </summary>
/// <remarks>
/// The reason belongs to the act of asking, not to the change itself, so it
/// rides alongside the command rather than inside it. The API fills this from
/// the request; a replay of an approved change leaves it empty, which is
/// harmless because an approved replay never reaches the gate.
/// </remarks>
public sealed class ProductChangeReason
{
    /// <summary>Gets or sets why the caller says the change is needed.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Marks the one dispatch that applies an already-approved change, so the gate
/// lets it through.
/// </summary>
/// <remarks>
/// Scoped to the approval request and set only by
/// <see cref="ApproveProductChangeCommandHandler"/> immediately around the
/// replay. Nothing reaching the API can set it.
/// </remarks>
public sealed class ProductChangeReplayScope
{
    /// <summary>Gets a value indicating whether an approved replay is running.</summary>
    public bool IsReplaying { get; private set; }

    /// <summary>Runs an approved replay with the gate suspended.</summary>
    /// <typeparam name="T">The replay's result type.</typeparam>
    /// <param name="replay">The replay to run.</param>
    /// <returns>Whatever the replay returned.</returns>
    public async Task<T> RunAsync<T>(Func<Task<T>> replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        IsReplaying = true;
        try
        {
            return await replay().ConfigureAwait(false);
        }
        finally
        {
            IsReplaying = false;
        }
    }
}

/// <summary>
/// The commands a held change may be replayed as, by name.
/// </summary>
/// <remarks>
/// A stored request names its command with a key from this table and is read
/// back only through it. Resolving a type from stored text instead would let
/// anything that could write a row choose a type to construct, so the mapping
/// is explicit and closed.
/// </remarks>
public static class ProductChangeRegistry
{
    private static readonly Dictionary<string, Type> ByKey = new(StringComparer.Ordinal)
    {
        ["product.update"] = typeof(UpdateProductCommand),
        ["product.activation"] = typeof(SetProductActivationCommand),
        ["product.barcode.add"] = typeof(AddProductBarcodeCommand),
        ["product.barcode.retire"] = typeof(RetireProductBarcodeCommand),
        ["product.barcode.primary"] = typeof(SetPrimaryProductBarcodeCommand),
        ["product.price.schedule"] = typeof(ScheduleProductPriceCommand),
        ["product.price.cancel"] = typeof(CancelScheduledProductPriceCommand),
        ["product.location-setting"] = typeof(SetProductLocationSettingCommand),
        ["product.unit-conversion.add"] = typeof(AddProductUnitConversionCommand),
        ["product.unit-conversion.remove"] = typeof(RemoveProductUnitConversionCommand),
        ["product.supplier.link"] = typeof(LinkProductSupplierCommand),
        ["product.supplier.unlink"] = typeof(UnlinkProductSupplierCommand),
    };

    private static readonly Dictionary<Type, string> ByType =
        ByKey.ToDictionary(pair => pair.Value, pair => pair.Key);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the registry key for a command type.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The key, or <see langword="null"/> when the command is not gated.</returns>
    public static string? KeyFor(object command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ByType.GetValueOrDefault(command.GetType());
    }

    /// <summary>Serializes a command for storage.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The serialized command.</returns>
    public static string Serialize(object command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return JsonSerializer.Serialize(command, command.GetType(), Json);
    }

    /// <summary>Reads a stored command back, through the whitelist.</summary>
    /// <param name="changeType">The registry key stored with the request.</param>
    /// <param name="payload">The serialized command.</param>
    /// <returns>The command, or why it could not be read.</returns>
    public static Result<object> Deserialize(string changeType, string payload)
    {
        if (!ByKey.TryGetValue(changeType ?? string.Empty, out Type? type))
        {
            return Result<object>.Failure(CatalogErrors.ChangeRequestNotReplayable(changeType));
        }

        try
        {
            object? command = JsonSerializer.Deserialize(payload, type, Json);
            return command is null
                ? Result<object>.Failure(CatalogErrors.ChangeRequestNotReplayable(changeType))
                : Result<object>.Success(command);
        }
        catch (JsonException)
        {
            return Result<object>.Failure(CatalogErrors.ChangeRequestNotReplayable(changeType));
        }
    }
}

/// <summary>
/// Holds a product change for the owner unless the caller may approve changes
/// themselves.
/// </summary>
/// <remarks>
/// <para>
/// This sits in the pipeline rather than in each handler so that every gated
/// command is covered by one rule, including any added later: a new product
/// mutation is gated the moment it is listed in
/// <see cref="ProductChangeRegistry"/>, with no endpoint to remember to change.
/// </para>
/// <para>
/// The change is held, not applied and reverted. An unapproved price or VAT
/// change that reached the registers even briefly would be charged to a real
/// customer, so nothing is written to the product until the owner says so.
/// </para>
/// </remarks>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public sealed class ProductChangeApprovalBehaviour<TMessage, TResult>(
    IProductChangeRequestRepository requests,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ProductChangeReason reason,
    ProductChangeReplayScope replay,
    ISystemClock clock) : IPipelineBehaviour<TMessage, TResult>
{
    /// <inheritdoc />
    public async Task<Result<TResult>> HandleAsync(
        TMessage message,
        Func<Task<Result<TResult>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (message is not IProductChangeCommand change || replay.IsReplaying)
        {
            return await next().ConfigureAwait(false);
        }

        string? changeType = ProductChangeRegistry.KeyFor(message);
        if (changeType is null)
        {
            return await next().ConfigureAwait(false);
        }

        UserId? userId = currentUser.UserId;
        if (userId is null)
        {
            return await next().ConfigureAwait(false);
        }

        bool mayApprove = await permissions
            .HasPermissionAsync(userId.Value, Permissions.Catalog.ApproveChanges, null, cancellationToken)
            .ConfigureAwait(false);

        if (mayApprove)
        {
            return await next().ConfigureAwait(false);
        }

        Result<ProductChangeRequest> raised = ProductChangeRequest.Create(
            change.ProductId,
            changeType,
            change.ChangeSummary,
            ProductChangeRegistry.Serialize(message!),
            reason.Reason,
            userId.Value,
            clock.UtcNow);

        if (raised.IsFailure)
        {
            return Result<TResult>.Failure(raised.Errors);
        }

        await requests.AddAsync(raised.Value, cancellationToken).ConfigureAwait(false);

        // Reported as a refusal so no caller mistakes it for the change having
        // been made; the code and metadata tell the UI it is pending, not wrong.
        return Result<TResult>.Failure(
            CatalogErrors.ChangeAwaitingApproval(raised.Value.Id, raised.Value.Summary));
    }
}
