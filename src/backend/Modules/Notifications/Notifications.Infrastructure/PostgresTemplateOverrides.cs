using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Notifications.Application;

namespace RetailCloud.Notifications.Infrastructure;

/// <summary>NOT-009 on notif.template_override: each change is the next version; the dispatcher uses the newest.</summary>
public sealed class PostgresTemplateOverrides : ITemplateOverrides
{
    public async Task<IReadOnlyList<TemplateOverrideView>> LatestAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        var latest = new Dictionary<string, TemplateOverrideView>(StringComparer.Ordinal);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT DISTINCT ON (template_code) template_code, version, store_name, signature, created_at FROM notif.template_override ORDER BY template_code, version DESC");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            latest[r.GetString(0)] = new TemplateOverrideView(r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4));
        }

        return [.. SmsTextRules.Codes.Select(c => latest.GetValueOrDefault(c) ?? new TemplateOverrideView(c, 0, null, null, null))];
    }

    public async Task<(TemplateChange Outcome, TemplateOverrideView? Current)> PublishAsync(IUnitOfWorkScope scope, string code, int expectedVersion,
        string? storeName, string? signature, Guid userId, CancellationToken cancellationToken)
    {
        if (!SmsTextRules.Codes.Contains(code))
        {
            return (TemplateChange.NotFound, null);
        }

        var s = (PostgresUnitOfWorkScope)scope;
        TemplateOverrideView current = (await LatestAsync(scope, cancellationToken)).Single(v => v.Code == code);
        if (current.Version != expectedVersion)
        {
            return (TemplateChange.VersionConflict, current);
        }

        await s.Transaction.SaveAsync("tpl", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command(
                "INSERT INTO notif.template_override (tenant_id, template_code, version, store_name, signature, created_by) " +
                "VALUES (core.current_tenant(), $1, $2, $3, $4, $5)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = code });
            cmd.Parameters.Add(new NpgsqlParameter { Value = expectedVersion + 1 });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)storeName?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)signature?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = userId });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await s.Transaction.ReleaseAsync("tpl", cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await s.Transaction.RollbackAsync("tpl", cancellationToken);
            return (TemplateChange.VersionConflict, (await LatestAsync(scope, cancellationToken)).Single(v => v.Code == code));
        }

        return (TemplateChange.Done, (await LatestAsync(scope, cancellationToken)).Single(v => v.Code == code));
    }
}
