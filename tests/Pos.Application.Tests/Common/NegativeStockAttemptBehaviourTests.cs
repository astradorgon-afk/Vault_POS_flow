using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Behaviours;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Tests.Common;

/// <summary>
/// The behaviour that writes refused stock draws after the unit of work ends
/// (ADR-0030): it must write them whatever the command's outcome, leave them to
/// the outermost message inside a batch, and never let a failed write change the
/// outcome the caller sees.
/// </summary>
public sealed class NegativeStockAttemptBehaviourTests
{
    private static readonly Error Refused = Error.Conflict("inventory.insufficient_stock", "Available 1, requested 6.");

    [Fact]
    public async Task RefusedCommand_FlushesTheAttempts_AndKeepsItsFailure()
    {
        FakeRecorder recorder = new() { HasPending = true };
        NegativeStockAttemptBehaviour<string, int> behaviour = NewBehaviour(recorder, activeTransaction: false);

        Result<int> result = await behaviour.HandleAsync(
            "DispatchTransferCommand", () => Task.FromResult(Result<int>.Failure(Refused)), CancellationToken.None);

        result.Error.Should().Be(Refused);
        recorder.Flushes.Should().Be(1);
    }

    [Fact]
    public async Task NothingRefused_FlushesNothing()
    {
        FakeRecorder recorder = new();
        NegativeStockAttemptBehaviour<string, int> behaviour = NewBehaviour(recorder, activeTransaction: false);

        Result<int> result = await behaviour.HandleAsync(
            "DispatchTransferCommand", () => Task.FromResult(Result<int>.Success(1)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        recorder.Flushes.Should().Be(0);
    }

    [Fact]
    public async Task InsideAnOuterTransaction_LeavesTheAttemptsForTheOutermostMessage()
    {
        FakeRecorder recorder = new() { HasPending = true };
        NegativeStockAttemptBehaviour<string, int> behaviour = NewBehaviour(recorder, activeTransaction: true);

        await behaviour.HandleAsync(
            "PostSaleCommand", () => Task.FromResult(Result<int>.Failure(Refused)), CancellationToken.None);

        recorder.Flushes.Should().Be(0, "writing now would share the batch's transaction and roll back with it");
    }

    [Fact]
    public async Task AFailedWrite_IsSwallowed_AndTheCallerStillSeesTheCommandOutcome()
    {
        FakeRecorder recorder = new() { HasPending = true, FlushError = new InvalidOperationException("database unavailable") };
        NegativeStockAttemptBehaviour<string, int> behaviour = NewBehaviour(recorder, activeTransaction: false);

        Result<int> result = await behaviour.HandleAsync(
            "DispatchTransferCommand", () => Task.FromResult(Result<int>.Failure(Refused)), CancellationToken.None);

        result.Error.Should().Be(Refused);
        recorder.Flushes.Should().Be(1);
    }

    private static NegativeStockAttemptBehaviour<string, int> NewBehaviour(FakeRecorder recorder, bool activeTransaction)
        => new(
            recorder,
            new FakeUnitOfWork(activeTransaction),
            NullLogger<NegativeStockAttemptBehaviour<string, int>>.Instance);

    private sealed class FakeRecorder : INegativeStockAttemptRecorder
    {
        public bool HasPending { get; set; }

        public IReadOnlyList<NegativeStockAttempt> Pending => [];

        public int Flushes { get; private set; }

        public Exception? FlushError { get; init; }

        public void Record(NegativeStockAttempt attempt) => HasPending = true;

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            HasPending = false;
            return FlushError is null ? Task.CompletedTask : Task.FromException(FlushError);
        }
    }

    private sealed class FakeUnitOfWork(bool activeTransaction) : IUnitOfWork
    {
        public bool HasActiveTransaction => activeTransaction;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("The behaviour never opens a transaction.");
    }
}
