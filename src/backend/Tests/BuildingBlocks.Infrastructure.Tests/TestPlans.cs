using Npgsql;

namespace RetailCloud.TestSupport;

/// <summary>
/// TEN-003: the application roles cannot change a plan, so tests that need more than the trial caps switch the tenant's
/// plan through an administrative connection (PGUSER / PGPASSWORD, the same database as RC_TEST_PG).
/// </summary>
public static class TestPlans
{
    public static async Task SetAsync(Guid tenant, string plan)
    {
        string cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused")
        {
            Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD"),
        }.ToString();
        await using var ds = NpgsqlDataSource.Create(cs);
        await using NpgsqlCommand cmd = ds.CreateCommand("UPDATE platform.subscription SET plan_code = $2, updated_at = now() WHERE tenant_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = tenant });
        cmd.Parameters.Add(new NpgsqlParameter { Value = plan });
        if (await cmd.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException($"tenant {tenant} has no subscription");
        }
    }
}
