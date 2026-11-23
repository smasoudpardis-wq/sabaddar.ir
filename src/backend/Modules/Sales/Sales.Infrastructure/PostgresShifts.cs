using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Sales.Application;

namespace RetailCloud.Sales.Infrastructure;

/// <summary>
/// UC-06 on sales.shift / sales.cash_movement. The database enforces one open shift per register (DI-008), immutable
/// facts and counts (RC017), cash movements only while open, the variance arithmetic and SoD-01 on the variance decision.
/// </summary>
public sealed class PostgresShifts(IOutbox outbox) : IShifts
{
    private const string Columns =
        "id, register_id, branch_id, cashier_id, status, opening_cash, expected_cash, counted_cash, variance, variance_status, variance_reason, opened_at, closed_at, version";

    public async Task<ShiftResult<ShiftView>> OpenAsync(IUnitOfWorkScope scope, Guid registerId, Guid cashier, long openingCash, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand open = s.Command("SELECT EXISTS (SELECT 1 FROM sales.shift WHERE register_id = $1 AND status <> 'Closed')"))
        {
            open.Parameters.Add(new NpgsqlParameter { Value = registerId });
            if ((bool)(await open.ExecuteScalarAsync(cancellationToken))!)
            {
                return new(ShiftOutcome.AlreadyOpen, null);   // DI-008 (the unique index decides races)
            }
        }

        Guid id = Guid.CreateVersion7();
        await using (NpgsqlCommand cmd = s.Command(
                         "INSERT INTO sales.shift (tenant_id, id, register_id, branch_id, cashier_id, opening_cash) VALUES (core.current_tenant(), $1, $2, $2, $3, $4)"))
        {
            // branch_id is replaced by the register's branch in sales.shift_register (the $2 placeholder only satisfies NOT NULL)
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = registerId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = cashier });
            cmd.Parameters.Add(new NpgsqlParameter { Value = openingCash });
            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "ux_shift_one_open")
            {
                return new(ShiftOutcome.AlreadyOpen, null);   // a simultaneous opening won; the caller's unit of work rolls back
            }
        }

        return new(ShiftOutcome.Done, await GetAsync(scope, id, cancellationToken));
    }

    public async Task<ShiftView?> GetAsync(IUnitOfWorkScope scope, Guid shiftId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command($"SELECT {Columns} FROM sales.shift WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = shiftId });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken))
        {
            return null;
        }

        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        string status = r.GetString(4);
        return new ShiftView(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), status, r.GetInt64(5), status == "Closed" ? r.GetInt64(6) : null,
            L(7), L(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetFieldValue<DateTimeOffset>(11),
            r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.GetInt32(13));
    }

    public async Task<ShiftResult<CashMovementView>> RecordCashAsync(IUnitOfWorkScope scope, Guid shiftId, bool cashIn, long amount, string reason, Guid by,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        if (await StatusAsync(s, shiftId, cancellationToken) is not { } status)
        {
            return new(ShiftOutcome.NotFound, null);
        }

        if (status != "Open")
        {
            return new(ShiftOutcome.NotOpen, null);
        }

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = s.Command(
            "INSERT INTO sales.cash_movement (tenant_id, id, shift_id, direction, amount, reason, recorded_by) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6) RETURNING recorded_at");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = shiftId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = cashIn ? "In" : "Out" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = amount });
        cmd.Parameters.Add(new NpgsqlParameter { Value = reason.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = by });
        var at = (DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return new(ShiftOutcome.Done, new CashMovementView(id, cashIn ? "In" : "Out", amount, reason.Trim(), new DateTimeOffset(at, TimeSpan.Zero)));
    }

    public async Task<ShiftResult<ShiftView>> CloseAsync(IUnitOfWorkScope scope, Guid shiftId, int expectedVersion, long countedCash, string? varianceReason,
        long approvalThreshold, Guid by, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        if (await StatusAsync(s, shiftId, cancellationToken) is not { } status)
        {
            return new(ShiftOutcome.NotFound, null);
        }

        if (status == "Closed")
        {
            return new(ShiftOutcome.NotOpen, null);
        }

        await using (NpgsqlCommand cmd = s.Command(
                         "WITH e AS (SELECT sales.shift_expected_cash($1) AS expected) " +
                         "UPDATE sales.shift SET status = 'Closed', expected_cash = e.expected, counted_cash = $3, variance = $3 - e.expected, " +
                         "variance_reason = $4, variance_status = CASE WHEN abs($3 - e.expected) > $5 THEN 'PendingApproval' ELSE 'None' END, " +
                         "closed_at = now(), closed_by = $6, version = version + 1 FROM e WHERE id = $1 AND version = $2 AND status <> 'Closed'"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = shiftId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = expectedVersion });
            cmd.Parameters.Add(new NpgsqlParameter { Value = countedCash });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)varianceReason?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = approvalThreshold });
            cmd.Parameters.Add(new NpgsqlParameter { Value = by });
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return new(ShiftOutcome.VersionConflict, null);
            }
        }

        ShiftView closed = (await GetAsync(scope, shiftId, cancellationToken))!;
        await outbox.AddAsync(scope, "Sales", "sales.shift-closed", 1, shiftId, new
        {
            shiftId, closed.RegisterId, closed.BranchId, closed.CashierId, closed.OpeningCash, closed.ExpectedCash, closed.CountedCash,
            closed.Variance, closed.VarianceStatus, closedAt = closed.ClosedAt,
        }, causationId: null, cancellationToken);
        return new(ShiftOutcome.Done, closed);
    }

    public async Task<ShiftResult<ShiftView>> DecideVarianceAsync(IUnitOfWorkScope scope, Guid shiftId, bool approve, Guid supervisor, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        ShiftView? shift = await GetAsync(scope, shiftId, cancellationToken);
        if (shift is null)
        {
            return new(ShiftOutcome.NotFound, null);
        }

        if (shift.VarianceStatus != "PendingApproval")
        {
            return new(ShiftOutcome.VarianceNotPending, null);
        }

        if (shift.CashierId == supervisor)
        {
            return new(ShiftOutcome.SelfApproval, null);   // SoD-01 (the database checks it as well)
        }

        await using (NpgsqlCommand cmd = s.Command(
                         "UPDATE sales.shift SET variance_status = $2, variance_decided_by = $3, variance_decided_at = now() " +
                         "WHERE id = $1 AND variance_status = 'PendingApproval'"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = shiftId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = approve ? "Approved" : "Rejected" });
            cmd.Parameters.Add(new NpgsqlParameter { Value = supervisor });
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return new(ShiftOutcome.VarianceNotPending, null);
            }
        }

        return new(ShiftOutcome.Done, await GetAsync(scope, shiftId, cancellationToken));
    }

    private static async Task<string?> StatusAsync(PostgresUnitOfWorkScope s, Guid shiftId, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command("SELECT status FROM sales.shift WHERE id = $1 FOR UPDATE");
        cmd.Parameters.Add(new NpgsqlParameter { Value = shiftId });
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
