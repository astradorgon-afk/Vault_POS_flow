using System.Text.Json;
using Pos.Domain.Common;
using Pos.Infrastructure.Sync;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Offline;

/// <summary>What one download run did.</summary>
/// <param name="Cursor">Where the device's cursor now stands.</param>
/// <param name="Applied">How many changes were written.</param>
/// <param name="Unreachable">Whether the run ended because the server could not be reached.</param>
/// <param name="RebaselineRequired">Whether the server refused the cursor outright.</param>
/// <param name="Problem">
/// Why the run stopped, when it stopped for a reason. Without it a page the
/// device could not read is indistinguishable from a quiet run with nothing to
/// do, and a register whose catalogue silently stopped updating is the worst
/// shape this can fail in.
/// </param>
public sealed record ChangeFeedDownloadOutcome(
    long Cursor,
    int Applied,
    bool Unreachable,
    bool RebaselineRequired,
    string? Problem = null);

/// <summary>
/// Fetches pages of the change feed and applies them, until there is nothing
/// left to fetch.
/// </summary>
/// <remarks>
/// <para>
/// This is the join that was missing: the server had a route and the device had
/// an applier, and nothing carried a page from one to the other. What it adds
/// beyond the carrying is the translation — the wire is a kind and a blob of
/// JSON, and the applier takes typed changes — and knowing when to stop.
/// </para>
/// <para>
/// A kind this build does not know is **refused**, not skipped. The alternative
/// is a register that quietly ignores half a feed and believes its catalogue is
/// current; a device behind its server should stop and say so, not carry on
/// selling from a cache with holes in it.
/// </para>
/// </remarks>
/// <param name="transport">How a page reaches the device.</param>
/// <param name="applier">What writes a page into the device's store.</param>
public sealed class ChangeFeedDownloader(ISyncTransport transport, ChangeFeedApplier applier)
{
    /// <summary>How many changes one page asks for.</summary>
    public const int PageSize = 500;

    /// <summary>The most pages one run fetches, so a run always ends.</summary>
    public const int MaxPagesPerRun = 50;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new StronglyTypedIdJsonConverter() },
    };

    /// <summary>Downloads and applies everything waiting.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the run did.</returns>
    public async Task<ChangeFeedDownloadOutcome> RunAsync(CancellationToken cancellationToken)
    {
        long cursor = await applier.ReadCursorAsync(cancellationToken).ConfigureAwait(false);
        int applied = 0;

        // A register that has applied nothing has no changes to ask for — the
        // feed carries changes, not a starting state — so it starts with one.
        if (cursor == 0)
        {
            ChangeFeedDownloadOutcome started = await BaselineAsync(cancellationToken).ConfigureAwait(false);

            if (started.Problem is not null || started.Unreachable)
            {
                return started;
            }

            cursor = started.Cursor;
            applied = started.Applied;
        }

        for (int page = 0; page < MaxPagesPerRun; page++)
        {
            Result<SyncPullResponse> fetched = await transport
                .PullAsync(cursor, PageSize, cancellationToken)
                .ConfigureAwait(false);

            if (fetched.IsFailure)
            {
                if (fetched.Error.Code != HttpSyncTransport.RebaselineRequired)
                {
                    return new ChangeFeedDownloadOutcome(cursor, applied, true, false, fetched.Error.Code);
                }

                // The server cannot serve this cursor any more, and the remedy is
                // never a retry. Starting again is the only honest answer, and
                // doing it here rather than reporting it means a register recovers
                // on its own instead of waiting for somebody to notice.
                ChangeFeedDownloadOutcome restarted = await BaselineAsync(cancellationToken).ConfigureAwait(false);

                return restarted with
                {
                    Applied = applied + restarted.Applied,
                    RebaselineRequired = true,
                };
            }

            SyncPullResponse body = fetched.Value;

            if (body.Changes.Count == 0 && body.NextCursor == cursor)
            {
                break;
            }

            Result<ChangeFeedPage> read = Translate(body);

            if (read.IsFailure)
            {
                // Nothing of this page is applied. A half-applied page would be
                // a cache that looks current and is not.
                return new ChangeFeedDownloadOutcome(cursor, applied, false, false, read.Error.Message);
            }

            Result<ChangeFeedApplyOutcome> written = await applier
                .ApplyAsync(read.Value, cancellationToken)
                .ConfigureAwait(false);

            if (written.IsFailure)
            {
                return new ChangeFeedDownloadOutcome(cursor, applied, false, false, written.Error.Message);
            }

            applied += written.Value.AppliedChanges;
            cursor = written.Value.Cursor;

            if (body.Changes.Count < PageSize)
            {
                break;
            }
        }

        return new ChangeFeedDownloadOutcome(cursor, applied, false, false);
    }

    /// <summary>Fetches a whole starting state and replaces the cache with it.</summary>
    private async Task<ChangeFeedDownloadOutcome> BaselineAsync(CancellationToken cancellationToken)
    {
        Result<SyncBaselineResponse> fetched = await transport
            .BaselineAsync(cancellationToken)
            .ConfigureAwait(false);

        if (fetched.IsFailure)
        {
            return new ChangeFeedDownloadOutcome(0, 0, true, false, fetched.Error.Code);
        }

        SyncBaselineResponse body = fetched.Value;

        // Read as a page of its own, running from nothing to however many rows
        // the baseline carries. Those numbers order the writing and nothing else;
        // where the device resumes is the cursor the server read the state at.
        Result<ChangeFeedPage> read = Translate(new SyncPullResponse(0, body.Changes.Count, body.Changes));

        if (read.IsFailure)
        {
            return new ChangeFeedDownloadOutcome(0, 0, false, false, read.Error.Message);
        }

        Result<ChangeFeedApplyOutcome> written = await applier
            .ApplyBaselineAsync(read.Value, body.ResumeCursor, cancellationToken)
            .ConfigureAwait(false);

        return written.IsFailure
            ? new ChangeFeedDownloadOutcome(0, 0, false, false, written.Error.Message)
            : new ChangeFeedDownloadOutcome(written.Value.Cursor, written.Value.AppliedChanges, false, false);
    }

    /// <summary>Turns a wire page into the typed changes the applier takes.</summary>
    private static Result<ChangeFeedPage> Translate(SyncPullResponse body)
    {
        List<ChangeFeedChange> changes = new(body.Changes.Count);

        foreach (SyncPullChange wire in body.Changes)
        {
            Type? kind = KindOf(wire.Kind);

            if (kind is null)
            {
                return Result<ChangeFeedPage>.Failure(ChangeFeedErrors.PageInvalid(
                    FormattableString.Invariant($"This device does not understand {wire.Kind} changes.")));
            }

            ChangeFeedChange? change;

            try
            {
                change = (ChangeFeedChange?)wire.Change.Deserialize(kind, ReadOptions);
            }
            catch (JsonException ex)
            {
                return Result<ChangeFeedPage>.Failure(ChangeFeedErrors.PageInvalid(ex.Message));
            }

            if (change is null)
            {
                return Result<ChangeFeedPage>.Failure(ChangeFeedErrors.PageInvalid(
                    FormattableString.Invariant($"A {wire.Kind} change could not be read.")));
            }

            changes.Add(change);
        }

        return Result<ChangeFeedPage>.Success(new ChangeFeedPage(body.FromCursor, body.NextCursor, changes));
    }

    /// <summary>
    /// The change kinds this build knows, by name.
    /// </summary>
    /// <remarks>
    /// An explicit list rather than a reflective lookup over the assembly: the
    /// names come off the wire, and a reflective one would let a page name any
    /// type in the process.
    /// </remarks>
    private static Type? KindOf(string kind) => kind switch
    {
        nameof(ProductChanged) => typeof(ProductChanged),
        nameof(ProductBarcodeChanged) => typeof(ProductBarcodeChanged),
        nameof(ProductPriceChanged) => typeof(ProductPriceChanged),
        nameof(ProductPriceRemoved) => typeof(ProductPriceRemoved),
        nameof(BatchChanged) => typeof(BatchChanged),
        nameof(LocationChanged) => typeof(LocationChanged),
        nameof(UserChanged) => typeof(UserChanged),
        nameof(PermissionSnapshotIssued) => typeof(PermissionSnapshotIssued),
        nameof(PermissionSnapshotRevoked) => typeof(PermissionSnapshotRevoked),
        nameof(SyncRetryRequested) => typeof(SyncRetryRequested),
        _ => null,
    };
}
