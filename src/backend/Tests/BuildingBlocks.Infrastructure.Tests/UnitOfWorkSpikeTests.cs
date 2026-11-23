using Microsoft.EntityFrameworkCore;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// SP-07 (D12): EF Core + Npgsql sharing one connection/transaction across module DbContexts, with transaction-local
/// tenant context under PostgreSQL RLS (D04 §5.3, ADR-002, ADR-004). Runs against the D05 schema as a non-superuser.
/// </summary>
public sealed class UnitOfWorkSpikeTests : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private PostgresUnitOfWork _uow = null!;
    private readonly TenantId _a = new(Guid.CreateVersion7());
    private readonly TenantId _b = new(Guid.CreateVersion7());

    public async Task InitializeAsync()
    {
        string? cs = Environment.GetEnvironmentVariable("RC_TEST_PG");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return;
        }

        _ds = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs) { MaxPoolSize = 4 }.ConnectionString);
        _uow = new PostgresUnitOfWork(_ds);
        foreach (TenantId t in new[] { _a, _b })
        {
            await _uow.ExecuteAsync(new FixedTenantContext(t), async (s, ct) =>
            {
                await using NpgsqlCommand cmd = Scope(s).Command(
                    "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'spike', 'general')");
                cmd.Parameters.Add(new NpgsqlParameter { Value = t.Value });
                return await cmd.ExecuteNonQueryAsync(ct);
            });
        }
    }

    public async Task DisposeAsync()
    {
        if (_ds is not null)
        {
            await _ds.DisposeAsync();
        }
    }

    private static PostgresUnitOfWorkScope Scope(IUnitOfWorkScope s) => (PostgresUnitOfWorkScope)s;

    private Task<T> Run<T>(TenantId t, Func<PostgresUnitOfWorkScope, CancellationToken, Task<T>> work) =>
        _uow.ExecuteAsync(new FixedTenantContext(t), (s, ct) => work(Scope(s), ct));

    private Task<int> CountBranches(TenantId t) =>
        Run(t, (s, ct) => s.Context<PlatformDb>().Branches.CountAsync(ct));

    [DbFact]
    public async Task Only_the_global_entitlement_code_becomes_EntitlementExceeded()
    {
        // RC030 is global (01_core.sql registry) and mapped centrally; RC001-RC029 are local and must reach the caller
        // untouched (e.g. RC013 is "closed accounting period", which must never read as "plan limit reached").
        EntitlementExceededException e = await Assert.ThrowsAsync<EntitlementExceededException>(() => Run(_a, async (s, ct) =>
        {
            await using var c = s.Command("DO $$ BEGIN RAISE EXCEPTION 'entitlement-exceeded:registers:4' USING ERRCODE = 'RC030'; END $$");
            return await c.ExecuteNonQueryAsync(ct);
        }));
        Assert.Equal(("registers", 4), (e.Resource, e.Limit));
        PostgresException local = await Assert.ThrowsAsync<PostgresException>(() => Run(_a, async (s, ct) =>
        {
            await using var c = s.Command("DO $$ BEGIN RAISE EXCEPTION 'period is Closed' USING ERRCODE = 'RC013'; END $$");
            return await c.ExecuteNonQueryAsync(ct);
        }));
        Assert.Equal("RC013", local.SqlState);
    }

    [DbFact]
    public async Task Two_module_contexts_commit_in_one_transaction()
    {
        Guid branch = Guid.CreateVersion7();
        await Run(_a, (s, ct) =>
        {
            s.Context<PlatformDb>().Branches.Add(new Branch { TenantId = _a.Value, Id = branch, Code = "B1", Name = "مرکزی" });
            s.Context<InventoryDb>().Warehouses.Add(new Warehouse { TenantId = _a.Value, Id = Guid.CreateVersion7(), BranchId = branch, Code = "W1" });
            return Task.FromResult(0);
        });

        Assert.Equal(1, await CountBranches(_a));
        Assert.Equal(1, await Run(_a, (s, ct) => s.Context<InventoryDb>().Warehouses.CountAsync(ct)));
    }

    [DbFact]
    public async Task An_exception_rolls_back_every_module()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run<int>(_a, async (s, ct) =>
        {
            s.Context<PlatformDb>().Branches.Add(new Branch { TenantId = _a.Value, Id = Guid.CreateVersion7(), Code = "RB", Name = "x" });
            await s.Context<PlatformDb>().SaveChangesAsync(ct); // flushed inside the transaction
            throw new InvalidOperationException("business failure after the first write");
        }));

        Assert.Equal(0, await CountBranches(_a));
    }

    [DbFact]
    public async Task A_constraint_failure_in_the_second_module_rolls_back_the_first()
    {
        // Cross-module references have no FK by design (D05 §3), so use a constraint inside inv:
        // warehouse code is unique per tenant.
        await Run(_a, (s, ct) =>
        {
            s.Context<InventoryDb>().Warehouses.Add(new Warehouse { TenantId = _a.Value, Id = Guid.CreateVersion7(), BranchId = Guid.NewGuid(), Code = "DUP" });
            return Task.FromResult(0);
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Run(_a, (s, ct) =>
        {
            s.Context<PlatformDb>().Branches.Add(new Branch { TenantId = _a.Value, Id = Guid.CreateVersion7(), Code = "FK", Name = "x" });
            s.Context<InventoryDb>().Warehouses.Add(new Warehouse { TenantId = _a.Value, Id = Guid.CreateVersion7(), BranchId = Guid.NewGuid(), Code = "DUP" });
            return Task.FromResult(0);
        }));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        Assert.Equal(0, await CountBranches(_a)); // the platform write was rolled back with the failed inventory write
    }

    [DbFact]
    public async Task Rls_hides_and_rejects_other_tenants_rows()
    {
        await Run(_a, (s, ct) =>
        {
            s.Context<PlatformDb>().Branches.Add(new Branch { TenantId = _a.Value, Id = Guid.CreateVersion7(), Code = "A1", Name = "a" });
            return Task.FromResult(0);
        });

        Assert.Equal(0, await CountBranches(_b));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Run(_b, (s, ct) =>
        {
            // tenant B tries to write a row that claims tenant A
            s.Context<PlatformDb>().Branches.Add(new Branch { TenantId = _a.Value, Id = Guid.CreateVersion7(), Code = "X", Name = "x" });
            return Task.FromResult(0);
        }));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [DbFact]
    public async Task Tenant_context_does_not_leak_to_the_next_user_of_a_pooled_connection()
    {
        await using var single = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RC_TEST_PG")) { MaxPoolSize = 1 }.ConnectionString);
        var uow = new PostgresUnitOfWork(single);
        await uow.ExecuteAsync(new FixedTenantContext(_a), (s, ct) =>
        {
            Scope(s).Context<PlatformDb>().Branches.Add(new Branch { TenantId = _a.Value, Id = Guid.CreateVersion7(), Code = "P1", Name = "p" });
            return Task.FromResult(0);
        });

        // same physical connection (pool size 1), used without a unit of work
        await using NpgsqlConnection raw = await single.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT coalesce(current_setting('app.tenant_id', true), ''), (SELECT count(*) FROM platform.branch)", raw);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("", r.GetString(0));
        Assert.Equal(0L, r.GetInt64(1));
    }

    [DbFact]
    public async Task Concurrent_units_of_work_for_different_tenants_stay_isolated()
    {
        var tenants = Enumerable.Range(0, 8).Select(_ => new TenantId(Guid.CreateVersion7())).ToList();
        foreach (TenantId t in tenants)
        {
            await Run(t, async (s, ct) =>
            {
                await using var c = s.Command("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'c', 'general')");
                c.Parameters.Add(new NpgsqlParameter { Value = t.Value });
                return await c.ExecuteNonQueryAsync(ct);
            });
            await RetailCloud.TestSupport.TestPlans.SetAsync(t.Value, "Enterprise");   // up to 8 branches each
        }

        int[] counts = await Task.WhenAll(tenants.Select(async (t, i) =>
        {
            await Run(t, (s, ct) =>
            {
                var db = s.Context<PlatformDb>();
                for (int k = 0; k <= i; k++)
                {
                    db.Branches.Add(new Branch { TenantId = t.Value, Id = Guid.CreateVersion7(), Code = $"C{k}", Name = "c" });
                }

                return Task.FromResult(0);
            });
            return await CountBranches(t);
        }));

        Assert.Equal(Enumerable.Range(1, tenants.Count), counts);
    }

    [DbFact]
    public async Task Guard_rejects_ef_commands_outside_a_transaction()
    {
        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync();
        var options = new DbContextOptionsBuilder<PlatformDb>().UseNpgsql(conn)
            .AddInterceptors(RequireTransactionInterceptor.Instance).Options;
        await using var db = new PlatformDb(options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Branches.CountAsync());
    }
}
