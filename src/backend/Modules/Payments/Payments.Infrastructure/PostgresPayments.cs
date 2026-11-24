using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Payments.Application;

namespace RetailCloud.Payments.Infrastructure;

/// <summary>PAY-001 / PAY-002 on pay.payment. The database holds the reference uniqueness (ux_payment_card_reference).</summary>
public sealed class PostgresPayments : IPayments
{
    private const string Columns =
        "id, method, status, amount, tendered, change_given, reference_no, terminal_no, card_last4, business_date, sale_id, override_approval_id, recorded_at";

    public async Task<PaymentView> RecordCashAsync(IUnitOfWorkScope scope, long amount, long tendered, PaymentOrigin origin, CancellationToken cancellationToken) =>
        (await InsertAsync((PostgresUnitOfWorkScope)scope, "Cash", "ConfirmedCash", amount, tendered, null, null, null, null, origin, cancellationToken))!;

    public async Task<PaymentResult> RecordManualCardAsync(IUnitOfWorkScope scope, long amount, string referenceNo, string? terminalNo, string? cardLast4,
        Guid? overrideApprovalId, PaymentOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var s = (PostgresUnitOfWorkScope)scope;
        await s.Transaction.SaveAsync("card_reference", cancellationToken);
        try
        {
            PaymentView p = (await InsertAsync(s, "ExternalCardTerminal", "ConfirmedManual", amount, null, referenceNo, terminalNo, cardLast4, overrideApprovalId,
                origin, cancellationToken))!;
            await s.Transaction.ReleaseAsync("card_reference", cancellationToken);
            return new PaymentResult(PaymentOutcome.Done, p);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "ux_payment_card_reference")
        {
            await s.Transaction.RollbackAsync("card_reference", cancellationToken);
            await using NpgsqlCommand cmd = s.Command(
                "SELECT id FROM pay.payment WHERE method = 'ExternalCardTerminal' AND coalesce(terminal_no, '') = coalesce($1, '') AND reference_no = $2 " +
                "AND business_date = $3 AND override_approval_id IS NULL");
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)terminalNo ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = referenceNo });
            cmd.Parameters.Add(new NpgsqlParameter { Value = origin.BusinessDate });
            return new PaymentResult(PaymentOutcome.DuplicateReference, null, (Guid?)await cmd.ExecuteScalarAsync(cancellationToken));
        }
    }

    public async Task VoidAsync(IUnitOfWorkScope scope, Guid paymentId, Guid by, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE pay.payment SET status = 'Voided', voided_at = now(), voided_by = $2 WHERE id = $1 AND status <> 'Voided'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = paymentId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = by });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentView>> ForSaleAsync(IUnitOfWorkScope scope, Guid saleId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command($"SELECT {Columns} FROM pay.payment WHERE sale_id = $1 ORDER BY recorded_at, id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
        var list = new List<PaymentView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(Read(r));
        }

        return list;
    }

    private static async Task<PaymentView?> InsertAsync(PostgresUnitOfWorkScope s, string method, string status, long amount, long? tendered, string? referenceNo,
        string? terminalNo, string? cardLast4, Guid? overrideApprovalId, PaymentOrigin origin, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command(
            "INSERT INTO pay.payment (tenant_id, id, method, status, amount, tendered, change_given, reference_no, terminal_no, card_last4, business_date, " +
            "sale_id, register_id, shift_id, recorded_by, override_approval_id) " +
            $"VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $5 - $4, $6, $7, $8, $9, $10, $11, $12, $13, $14) RETURNING {Columns}");
        object?[] values =
        [
            Guid.CreateVersion7(), method, status, amount, tendered, referenceNo, terminalNo, cardLast4, origin.BusinessDate, origin.SaleId, origin.RegisterId,
            origin.ShiftId, origin.RecordedBy, overrideApprovalId,
        ];
        NpgsqlDbType?[] types =
        [
            null, null, null, NpgsqlDbType.Bigint, NpgsqlDbType.Bigint, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Date,
            NpgsqlDbType.Uuid, NpgsqlDbType.Uuid, NpgsqlDbType.Uuid, null, NpgsqlDbType.Uuid,
        ];
        for (int i = 0; i < values.Length; i++)
        {
            cmd.Parameters.Add(types[i] is { } t ? new NpgsqlParameter { Value = values[i] ?? DBNull.Value, NpgsqlDbType = t } : new NpgsqlParameter { Value = values[i] });
        }

        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static PaymentView Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            r.GetFieldValue<DateOnly>(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.GetFieldValue<DateTimeOffset>(12));
}
