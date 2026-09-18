namespace Pos.Infrastructure.Sync;

/// <summary>The durable payload shape for an offline transfer receipt.</summary>
public sealed record TransferReceiveSyncPayload(
    Guid TransferId,
    IReadOnlyList<TransferReceiveSyncLine> Receives);

/// <summary>One transfer line counted by the receiving device.</summary>
public sealed record TransferReceiveSyncLine(
    int LineNo,
    Guid? BatchId,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);
