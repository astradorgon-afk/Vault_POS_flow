using Microsoft.AspNetCore.Components;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// The owner's queue of catalogue changes staff have asked for.
/// </summary>
/// <remarks>
/// A change listed here has not been applied. The command that would have run
/// is held, and approving it is what releases it, so an unapproved price or tax
/// change never reaches a register even briefly.
/// </remarks>
public sealed partial class ProductChangeRequests
{
    [Inject] private VaultFlowApiClient Api { get; set; } = default!;

    [Inject] private UserSession Session { get; set; } = default!;

    private readonly List<PosProductChangeRequest> requests = [];
    private readonly Dictionary<Guid, string> notes = [];

    private bool loading = true;
    private bool working;
    private bool showDecided;
    private string? message;

    private bool CanApprove => Session.HasPermission("product.change.approve");

    private Guid CurrentUserId => Session.Current?.User.Id ?? Guid.Empty;

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => ReloadAsync();

    private string NoteFor(Guid requestId) => notes.GetValueOrDefault(requestId, string.Empty);

    private void SetNote(Guid requestId, string? value) => notes[requestId] = value ?? string.Empty;

    private async Task ReloadAsync()
    {
        loading = true;
        message = null;
        try
        {
            ApiResult<List<PosProductChangeRequest>> result = await Api
                .GetProductChangeRequestsAsync(!showDecided, null, CancellationToken.None)
                .ConfigureAwait(false);

            requests.Clear();
            if (result is { IsSuccess: true, Value: { } rows })
            {
                requests.AddRange(rows);
            }
            else
            {
                message = result.Error ?? "The approval queue could not be loaded.";
            }
        }
        finally
        {
            loading = false;
        }
    }

    private async Task ApproveAsync(PosProductChangeRequest request)
    {
        await DecideAsync(
            () => Api.ApproveProductChangeAsync(request.RequestId, NoteFor(request.RequestId), CancellationToken.None),
            $"{request.Summary} was approved and applied to {request.ProductName}.")
            .ConfigureAwait(false);
    }

    private async Task RejectAsync(PosProductChangeRequest request)
    {
        if (string.IsNullOrWhiteSpace(NoteFor(request.RequestId)))
        {
            message = "Rejecting a change needs a note saying why.";
            return;
        }

        await DecideAsync(
            () => Api.RejectProductChangeAsync(request.RequestId, NoteFor(request.RequestId), CancellationToken.None),
            $"{request.Summary} was rejected.")
            .ConfigureAwait(false);
    }

    private async Task WithdrawAsync(PosProductChangeRequest request)
    {
        working = true;
        message = null;
        try
        {
            ApiResult<string> result = await Api
                .WithdrawProductChangeAsync(request.RequestId, CancellationToken.None)
                .ConfigureAwait(false);

            message = result.IsSuccess ? "The request was withdrawn." : result.Error;
        }
        finally
        {
            working = false;
        }

        await ReloadAsync().ConfigureAwait(false);
    }

    private async Task DecideAsync(Func<Task<ApiResult<PosReference>>> decide, string success)
    {
        working = true;
        message = null;
        try
        {
            ApiResult<PosReference> result = await decide().ConfigureAwait(false);

            // A held command can stop fitting while it waits — the barcode gets
            // taken, the price window closes. Say so rather than reporting an
            // approval that changed nothing.
            message = result.IsSuccess ? success : result.Error;
        }
        finally
        {
            working = false;
        }

        await ReloadAsync().ConfigureAwait(false);
    }
}
