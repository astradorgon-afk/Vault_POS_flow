using System.Net.Http.Json;
using System.Text.Json;
using Pos.Domain.Common;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Carries a batch to <c>POST /api/v1/sync/push</c> over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The base address and the device's credentials belong to the
/// <see cref="HttpClient"/> the caller configures, not to this type. That is the
/// ordinary .NET split, and it keeps one question out of here that the uploader
/// must never answer differently per call: who this register is.
/// </para>
/// <para>
/// Every failure is reported as one, and never as an empty batch of verdicts.
/// The uploader's whole retry policy rests on being able to tell "the server
/// said nothing about my events" from "the server said no": the first keeps the
/// events and tries again, the second stops asking. A transport that swallowed a
/// timeout into a successful-looking response would silently discard a shift's
/// worth of sales.
/// </para>
/// </remarks>
/// <param name="client">A client already pointed at the server and authenticated as this device.</param>
public sealed class HttpSyncTransport(HttpClient client) : ISyncTransport
{
    /// <summary>The upload route.</summary>
    public const string PushPath = "api/v1/sync/push";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<Result<SyncPushResponse>> PushAsync(
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HttpResponseMessage response;

        try
        {
            response = await client
                .PostAsJsonAsync(PushPath, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Result<SyncPushResponse>.Failure(Error.Unavailable("sync.unreachable", ex.Message));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A client-side timeout, not the caller giving up. It reads as a
            // cancellation, and treating it as one would abandon the batch
            // without scheduling a retry.
            return Result<SyncPushResponse>.Failure(Error.Unavailable("sync.timeout", ex.Message));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Including 401 and 403. A device whose enrolment was revoked
                // keeps its events and keeps asking: the answer changes when
                // somebody re-enrols it, and throwing the queue away because of
                // a credential problem would lose the trading it did.
                return Result<SyncPushResponse>.Failure(Error.Unavailable(
                    "sync.http_" + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FormattableString.Invariant($"The server answered {(int)response.StatusCode} {response.StatusCode}.")));
            }

            try
            {
                SyncPushResponse? answered = await response.Content
                    .ReadFromJsonAsync<SyncPushResponse>(ReadOptions, cancellationToken)
                    .ConfigureAwait(false);

                return answered is null || answered.Results is null
                    ? Result<SyncPushResponse>.Failure(Error.Unavailable(
                        "sync.response_unreadable",
                        "The server answered with something this device could not read."))
                    : Result<SyncPushResponse>.Success(answered);
            }
            catch (JsonException ex)
            {
                return Result<SyncPushResponse>.Failure(
                    Error.Unavailable("sync.response_unreadable", ex.Message));
            }
        }
    }
}
