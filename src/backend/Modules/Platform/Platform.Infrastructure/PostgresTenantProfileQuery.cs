using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

public sealed class PostgresTenantProfileQuery(IUnitOfWork unitOfWork) : ITenantProfileQuery
{
    public Task<TenantProfile?> GetAsync(TenantId tenant, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(new FixedTenantContext(tenant), (s, ct) => ReadAsync((PostgresUnitOfWorkScope)s, ct), cancellationToken);

    /// <summary>The context tenant with its plan (no WHERE on id: RLS limits platform.tenant to the context tenant).</summary>
    internal static async Task<TenantProfile?> ReadAsync(PostgresUnitOfWorkScope s, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command(
            "SELECT t.id, t.legal_name, t.industry_code, t.time_zone, t.base_currency, t.pricing_mode, t.status, t.version, " +
            "coalesce(sub.plan_code, 'None'), sub.trial_ends_at, p.max_branches, p.max_registers, p.max_users " +
            "FROM platform.tenant t LEFT JOIN platform.subscription sub ON sub.tenant_id = t.id LEFT JOIN platform.plan p ON p.code = sub.plan_code");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        int? N(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        return new TenantProfile(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4).Trim(),
            r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
            new Entitlements(N(10), N(11), N(12)));
    }
}
