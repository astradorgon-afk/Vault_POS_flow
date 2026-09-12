using FluentAssertions;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Behaviours;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Tests.Common;

/// <summary>
/// The unit-of-work behaviour: every command is one transaction, and an
/// optimistic-concurrency failure becomes a retryable command failure rather
/// than an exception escaping the pipeline.
/// </summary>
public sealed class UnitOfWorkBehaviourTests
{
    private const string CommandName = "TestCommand";

    [Fact]
    public async Task ASuccessfulCommand_SavesAndCommits()
    {
        (UnitOfWorkBehaviour<string, int> behaviour, FakeUnitOfWork unitOfWork) = NewBehaviour();

        Result<int> result = await behaviour.HandleAsync(
            CommandName,
            () => Task.FromResult(Result<int>.Success(42)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);

        unitOfWork.SaveCount.Should().Be(1);
        unitOfWork.Transaction.Committed.Should().BeTrue();
        unitOfWork.Transaction.RolledBack.Should().BeFalse();
    }

    [Fact]
    public async Task ACommandFailure_RollsBack_WithoutSaving()
    {
        (UnitOfWorkBehaviour<string, int> behaviour, FakeUnitOfWork unitOfWork) = NewBehaviour();
        Error expected = Error.Validation("test.failed", "Command refused.");

        Result<int> result = await behaviour.HandleAsync(
            CommandName,
            () => Task.FromResult(Result<int>.Failure(expected)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(expected);

        unitOfWork.SaveCount.Should().Be(0, because: "a failure must not write partial work");
        unitOfWork.Transaction.RolledBack.Should().BeTrue();
        unitOfWork.Transaction.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task ContentionOnSave_BecomesTheBalanceContentionError_AndRollsBack()
    {
        (UnitOfWorkBehaviour<string, int> behaviour, FakeUnitOfWork unitOfWork) = NewBehaviour();
        unitOfWork.SaveError = new ConcurrencyConflictException();

        Result<int> result = await behaviour.HandleAsync(
            CommandName,
            () => Task.FromResult(Result<int>.Success(42)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(InventoryErrors.BalanceContention.Code);

        unitOfWork.Transaction.RolledBack.Should().BeTrue();
        unitOfWork.Transaction.Committed.Should().BeFalse();

        // The behaviour ran the command: the contention happened at the save.
        unitOfWork.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task ACommandInsideAnOpenTransaction_IsPassedThroughUnchanged()
    {
        (UnitOfWorkBehaviour<string, int> behaviour, FakeUnitOfWork unitOfWork) = NewBehaviour();
        unitOfWork.HasActiveTransaction = true;

        Result<int> result = await behaviour.HandleAsync(
            CommandName,
            () => Task.FromResult(Result<int>.Success(7)),
            CancellationToken.None);

        result.Value.Should().Be(7);
        unitOfWork.SaveCount.Should().Be(0, because: "an outer transaction owns the save and commit");
        unitOfWork.Transaction.Committed.Should().BeFalse();
        unitOfWork.Transaction.RolledBack.Should().BeFalse();
    }

    private static (UnitOfWorkBehaviour<string, int>, FakeUnitOfWork) NewBehaviour()
    {
        FakeUnitOfWork unitOfWork = new();
        return (new UnitOfWorkBehaviour<string, int>(unitOfWork), unitOfWork);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public FakeTransaction Transaction { get; } = new();

        public bool HasActiveTransaction { get; set; }

        public int SaveCount { get; private set; }

        public Exception? SaveError { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;

            return SaveError is not null
                ? Task.FromException<int>(SaveError)
                : Task.FromResult(1);
        }

        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
            => Task.FromResult<IUnitOfWorkTransaction>(Transaction);
    }

    private sealed class FakeTransaction : IUnitOfWorkTransaction
    {
        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}