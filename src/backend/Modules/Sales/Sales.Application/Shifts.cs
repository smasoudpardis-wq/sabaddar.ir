using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Sales.Application;

/// <summary>UC-06 / SAL-009: a register shift. <see cref="ExpectedCash"/> is only known once counted (blind close, D-24).</summary>
public sealed record ShiftView(Guid Id, Guid RegisterId, Guid BranchId, Guid CashierId, string Status, long OpeningCash, long? ExpectedCash,
    long? CountedCash, long? Variance, string VarianceStatus, string? VarianceReason, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, int Version);

public sealed record CashMovementView(Guid Id, string Direction, long Amount, string Reason, DateTimeOffset RecordedAt);

public enum ShiftOutcome
{
    Done,
    NotFound,
    AlreadyOpen,
    NotOpen,
    VersionConflict,
    VarianceNotPending,
    SelfApproval,
}

public sealed record ShiftResult<T>(ShiftOutcome Outcome, T? Value);

public interface IShifts
{
    Task<ShiftResult<ShiftView>> OpenAsync(IUnitOfWorkScope scope, Guid registerId, Guid cashier, long openingCash, CancellationToken cancellationToken);

    /// <summary>The shift as stored (with the expected cash); callers hide it before the count (D-24).</summary>
    Task<ShiftView?> GetAsync(IUnitOfWorkScope scope, Guid shiftId, CancellationToken cancellationToken);

    /// <summary>The register's shift that is not yet closed (at most one, DI-008), if any.</summary>
    Task<ShiftView?> CurrentAsync(IUnitOfWorkScope scope, Guid registerId, CancellationToken cancellationToken);

    Task<ShiftResult<CashMovementView>> RecordCashAsync(IUnitOfWorkScope scope, Guid shiftId, bool cashIn, long amount, string reason, Guid by,
        CancellationToken cancellationToken);

    /// <summary>Counts and closes: the database computes the expected cash; a variance beyond <paramref name="approvalThreshold"/> waits for a supervisor.</summary>
    Task<ShiftResult<ShiftView>> CloseAsync(IUnitOfWorkScope scope, Guid shiftId, int expectedVersion, long countedCash, string? varianceReason,
        long approvalThreshold, Guid by, CancellationToken cancellationToken);

    Task<ShiftResult<ShiftView>> DecideVarianceAsync(IUnitOfWorkScope scope, Guid shiftId, bool approve, Guid supervisor, CancellationToken cancellationToken);
}
