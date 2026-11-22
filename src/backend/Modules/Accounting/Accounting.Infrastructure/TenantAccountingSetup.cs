using Npgsql;
using RetailCloud.Accounting.Application;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Accounting.Infrastructure;

/// <summary>
/// TEN-001.1: default chart of accounts for the industry, the role mapping of D-30/D-59 and the twelve Open periods of
/// the current Jalali fiscal year in the tenant's time zone. Idempotent (inbox + ON CONFLICT).
/// </summary>
public sealed class TenantAccountingSetupConsumer : IIntegrationEventConsumer
{
    public string Consumer => "accounting.tenant-setup";

    public bool CanHandle(string eventType, int eventVersion) => eventType == "platform.tenant-registered" && eventVersion == 1;

    public async Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var s = (PostgresUnitOfWorkScope)scope;
        string industry = integrationEvent.Payload.GetProperty("industryCode").GetString()!;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(integrationEvent.Payload.GetProperty("timeZone").GetString()!);
        DateTimeOffset registeredAt = integrationEvent.Payload.GetProperty("registeredAt").GetDateTimeOffset();

        foreach (AccountTemplate a in ChartOfAccountsTemplate.For(industry))
        {
            await using NpgsqlCommand acc = s.Command(
                "INSERT INTO acc.account (tenant_id, id, code, name, account_type, parent_id, is_system, requires_detail, is_postable) " +
                "VALUES (core.current_tenant(), $1, $2, $3, $4, (SELECT id FROM acc.account WHERE code = $5), $6, $7, $8) " +
                "ON CONFLICT (tenant_id, code) DO NOTHING");
            acc.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            acc.Parameters.Add(new NpgsqlParameter { Value = a.Code });
            acc.Parameters.Add(new NpgsqlParameter { Value = a.Name });
            acc.Parameters.Add(new NpgsqlParameter { Value = a.Type.ToString() });
            acc.Parameters.Add(new NpgsqlParameter { Value = (object?)a.ParentCode ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            acc.Parameters.Add(new NpgsqlParameter { Value = a.Role is not null });
            acc.Parameters.Add(new NpgsqlParameter { Value = a.RequiresDetail });
            acc.Parameters.Add(new NpgsqlParameter { Value = a.Postable });
            await acc.ExecuteNonQueryAsync(cancellationToken);

            if (a.Role is not null)
            {
                await using NpgsqlCommand role = s.Command(
                    "INSERT INTO acc.account_role (tenant_id, role, account_id) SELECT core.current_tenant(), $1, id FROM acc.account WHERE code = $2 " +
                    "ON CONFLICT (tenant_id, role) DO NOTHING");
                role.Parameters.Add(new NpgsqlParameter { Value = a.Role });
                role.Parameters.Add(new NpgsqlParameter { Value = a.Code });
                await role.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (FiscalPeriodSpan p in FiscalCalendar.Periods(FiscalCalendar.FiscalYearOf(registeredAt, tz), tz))
        {
            await using NpgsqlCommand per = s.Command(
                "INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span) " +
                "VALUES (core.current_tenant(), $1, $2, $3, tstzrange($4, $5, '[)')) ON CONFLICT (tenant_id, fiscal_year, period_no) DO NOTHING");
            per.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            per.Parameters.Add(new NpgsqlParameter { Value = p.FiscalYear });
            per.Parameters.Add(new NpgsqlParameter { Value = p.PeriodNo });
            per.Parameters.Add(new NpgsqlParameter { Value = p.StartUtc });
            per.Parameters.Add(new NpgsqlParameter { Value = p.EndUtc });
            await per.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public sealed class ChartOfAccountsSetupStep : ITenantSetupStep
{
    public string Name => "chart-of-accounts";

    public async Task<bool> IsCompleteAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT count(*) FROM acc.account_role");
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))! == ChartOfAccountsTemplate.Roles.Count;
    }
}

public sealed class FiscalPeriodsSetupStep : ITenantSetupStep
{
    public string Name => "fiscal-periods";

    public async Task<bool> IsCompleteAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT EXISTS (SELECT 1 FROM acc.fiscal_period WHERE span @> now() AND status = 'Open')");
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }
}
