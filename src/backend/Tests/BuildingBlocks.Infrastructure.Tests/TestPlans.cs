using Npgsql;

namespace RetailCloud.TestSupport;

/// <summary>
/// TEN-003: the application roles cannot change a plan, so tests that need more than the trial caps switch the tenant's
/// plan through an administrative connection (PGUSER / PGPASSWORD, the same database as RC_TEST_PG).
/// </summary>
public static class TestPlans
{
    public static Task SetAsync(Guid tenant, string plan) =>
        AdminAsync("UPDATE platform.subscription SET plan_code = $2, updated_at = now() WHERE tenant_id = $1", tenant, plan);

    /// <summary>TEN-004: the trial ended a minute ago (the tenant must be on the trial plan).</summary>
    public static Task ExpireTrialAsync(Guid tenant) =>
        AdminAsync("UPDATE platform.subscription SET plan_code = 'Trial', trial_ends_at = now() - interval '1 minute' WHERE tenant_id = $1", tenant);

    /// <summary>TEN-004: Active, Suspended or Closed (a platform operation; the application cannot do it).</summary>
    public static Task SetStatusAsync(Guid tenant, string status) =>
        AdminAsync("UPDATE platform.tenant SET status = $2 WHERE id = $1", tenant, status);

    private static async Task AdminAsync(string sql, params object[] args)
    {
        string cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused")
        {
            Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD"),
        }.ToString();
        await using var ds = NpgsqlDataSource.Create(cs);
        await using NpgsqlCommand cmd = ds.CreateCommand(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        if (await cmd.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException($"no row changed for {args[0]}");
        }
    }
}
