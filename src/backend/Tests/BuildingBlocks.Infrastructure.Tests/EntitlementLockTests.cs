using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>TEN-003: the cap check is serialised per tenant, so the last slot cannot be taken twice (deterministic race).</summary>
public sealed class EntitlementLockTests
{
    private static async Task<(NpgsqlConnection, NpgsqlTransaction)> Open(Guid tenant)
    {
        var c = new NpgsqlConnection(Environment.GetEnvironmentVariable("RC_TEST_PG"));
        await c.OpenAsync();
        NpgsqlTransaction tx = await c.BeginTransactionAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", c, tx) { Parameters = { new() { Value = tenant.ToString() } } };
        await set.ExecuteNonQueryAsync();
        return (c, tx);
    }

    private static NpgsqlCommand Branch(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, string code) =>
        new("INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, $3, 'x')", c, tx)
        {
            Parameters = { new() { Value = tenant }, new() { Value = Guid.CreateVersion7() }, new() { Value = code } },
        };

    [DbFact]
    public async Task A_second_transaction_waits_for_the_first_and_then_finds_the_cap_reached()
    {
        Guid tenant = Guid.CreateVersion7();
        await using var ds = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG")!);
        await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand t = ((PostgresUnitOfWorkScope)s).Command("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'قفل', 'general')");
            t.Parameters.Add(new NpgsqlParameter { Value = tenant });
            return await t.ExecuteNonQueryAsync(ct);
        });   // trial: 2 branches

        (NpgsqlConnection c1, NpgsqlTransaction tx1) = await Open(tenant);
        (NpgsqlConnection c2, NpgsqlTransaction tx2) = await Open(tenant);
        await using (c1)
        await using (c2)
        {
            await using (NpgsqlCommand b = Branch(c1, tx1, tenant, "A1")) { await b.ExecuteNonQueryAsync(); }
            await using (NpgsqlCommand b = Branch(c1, tx1, tenant, "A2")) { await b.ExecuteNonQueryAsync(); }   // the last slot, not committed

            await using NpgsqlCommand second = Branch(c2, tx2, tenant, "B1");
            Task<int> racing = second.ExecuteNonQueryAsync();
            await Task.Delay(500);
            Assert.False(racing.IsCompleted, "the second transaction must wait for the first one's cap check");

            await tx1.CommitAsync();
            PostgresException e = await Assert.ThrowsAsync<PostgresException>(() => racing);
            Assert.Equal("RC030", e.SqlState);
        }
    }
}
