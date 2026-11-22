using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

public sealed class PostgresTenantProfileQuery(IUnitOfWork unitOfWork) : ITenantProfileQuery
{
    public Task<TenantProfile?> GetAsync(TenantId tenant, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<TenantProfile?>(new FixedTenantContext(tenant), async (s, ct) =>
        {
            // No WHERE on id: RLS (policy tenant_isolation on platform.tenant) already limits the result to the context tenant.
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT id, legal_name, industry_code, time_zone, base_currency, pricing_mode, status, version FROM platform.tenant");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? new TenantProfile(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4).Trim(),
                    r.GetString(5), r.GetString(6), r.GetInt32(7))
                : null;
        }, cancellationToken);
}
