using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.Inventory.Application;
using RetailCloud.Inventory.Infrastructure;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>US-061 / INV-001..006 (AT-040, AT-041, AT-042, AT-045, AT-046) and INV-004 locking, on the real database as rc_app.</summary>
public sealed class StockLedgerTests
{
    private static string App => Environment.GetEnvironmentVariable("RC_TEST_PG")!;

    private static readonly PostgresStockLedger Ledger = new();

    private sealed record Store(Guid Tenant, Guid Branch, Guid Warehouse);

    private static async Task<Store> StoreAsync()
    {
        var store = new Store(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await In(store, async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'انبار', 'general')", store.Tenant);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'W1')", store.Tenant, store.Warehouse, store.Branch);
            return 0;
        });
        return store;
    }

    private static async Task<T> In<T>(Store store, Func<IUnitOfWorkScope, CancellationToken, Task<T>> work)
    {
        await using var ds = NpgsqlDataSource.Create(App);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(store.Tenant)), work);
    }

    private static async Task<int> Exec(IUnitOfWorkScope s, CancellationToken ct, string sql, params object[] args)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> Scalar(Store store, string sql) =>
        await In(store, async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(sql);
            return Convert.ToInt64(await c.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        });

    private static Task<StockResult> Post(Store store, string type, params StockLine[] lines) =>
        In(store, (s, ct) => Ledger.PostAsync(s, new StockDocument(type, Guid.CreateVersion7(), DateTimeOffset.UtcNow, lines), ct));

    private static StockLine In(Store st, Guid variant, decimal qty, long cost, int line = 1) =>
        new(line, st.Warehouse, variant, MovementType.GoodsReceipt, qty, cost);

    private static StockLine Out(Store st, Guid variant, decimal qty, int line = 1) =>
        new(line, st.Warehouse, variant, MovementType.AdjustmentOut, -qty);

    private static async Task<StockBalanceView> Balance(Store st, Guid variant) =>
        (await In(st, (s, ct) => Ledger.BalancesAsync(s, st.Warehouse, variant, ct))).Single();

    [DbFact]
    public async Task AT040_AT046_movements_and_balance_move_together_with_the_average_cost()
    {
        Store st = await StoreAsync();
        Guid tea = Guid.CreateVersion7();
        Assert.Equal(StockOutcome.Done, (await Post(st, "GoodsReceipt", In(st, tea, 10, 1_000_000))).Outcome);
        Assert.Equal(StockOutcome.Done, (await Post(st, "GoodsReceipt", In(st, tea, 5, 650_000))).Outcome);
        StockResult issue = await Post(st, "Adjustment", Out(st, tea, 4));
        Assert.Equal((110_000L, -440_000L), (issue.Movements[0].UnitCost, issue.Movements[0].TotalCost));

        StockBalanceView b = await Balance(st, tea);
        Assert.Equal((11m, 110_000L, 1_210_000L), (b.OnHand, b.Wac, b.Value));
        // INV-001: the balance is exactly the ledger, in quantity and in value (oracle O-2)
        Assert.Equal(0, await Scalar(st,
            "SELECT count(*) FROM inv.stock_balance b WHERE b.on_hand <> (SELECT sum(qty_base) FROM inv.stock_movement m WHERE m.variant_id = b.variant_id) " +
            "OR b.value_on_hand <> (SELECT sum(total_cost) FROM inv.stock_movement m WHERE m.variant_id = b.variant_id)"));
        Assert.Equal(3, (await In(st, (s, ct) => Ledger.MovementsAsync(s, tea, null, 50, ct))).Count);
    }

    [DbFact]
    public async Task AT041_a_source_document_posts_once()
    {
        Store st = await StoreAsync();
        Guid v = Guid.CreateVersion7(), source = Guid.CreateVersion7();
        var doc = new StockDocument("GoodsReceipt", source, DateTimeOffset.UtcNow, [In(st, v, 3, 3000)]);
        Assert.Equal(StockOutcome.Done, (await In(st, (s, ct) => Ledger.PostAsync(s, doc, ct))).Outcome);
        Assert.Equal(StockOutcome.AlreadyPosted, (await In(st, (s, ct) => Ledger.PostAsync(s, doc, ct))).Outcome);
        Assert.Equal(3m, (await Balance(st, v)).OnHand);
        Assert.Equal(1, await Scalar(st, "SELECT count(*) FROM inv.stock_movement"));
    }

    [DbFact]
    public async Task AT042_forbid_refuses_a_shortage_with_every_item_listed_and_leaves_nothing_behind()
    {
        Store st = await StoreAsync();
        Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7();
        await Post(st, "GoodsReceipt", In(st, a, 2, 2000, 1), In(st, b, 1, 1000, 2));
        StockResult r = await Post(st, "Adjustment", Out(st, a, 3, 1), Out(st, b, 5, 2));
        Assert.Equal(StockOutcome.InsufficientStock, r.Outcome);
        Assert.Equal([(a, 2m, 3m), (b, 1m, 5m)], r.Shortages.OrderBy(x => x.Available).Reverse().Select(x => (x.VariantId, x.Available, x.Requested)));
        Assert.Equal(2m, (await Balance(st, a)).OnHand);
        Assert.Equal(2, await Scalar(st, "SELECT count(*) FROM inv.stock_movement"));
        Assert.Equal(1, await Scalar(st, "SELECT count(*) FROM inv.posted_source"));   // the refused document can be posted later

        // AllowWithWarning for the store: allowed, with the D07 warning; a branch rule is more specific than the store's
        await In(st, async (s, ct) => { await Ledger.SetPolicyAsync(s, "Tenant", st.Tenant, NegativeStockPolicy.AllowWithWarning, Guid.NewGuid(), ct); return 0; });
        StockResult allowed = await Post(st, "Adjustment", Out(st, a, 3));
        Assert.Equal(StockOutcome.Done, allowed.Outcome);
        Assert.Equal(["negative-stock-allowed"], allowed.Warnings);
        Assert.Equal(-1m, (await Balance(st, a)).OnHand);
        await In(st, async (s, ct) => { await Ledger.SetPolicyAsync(s, "Branch", st.Branch, NegativeStockPolicy.Forbid, Guid.NewGuid(), ct); return 0; });
        Assert.Equal(StockOutcome.InsufficientStock, (await Post(st, "Adjustment", Out(st, b, 2))).Outcome);

        // under Forbid again, goods can still come in to a short balance (it was allowed when it went short)
        Assert.Equal(StockOutcome.Done, (await Post(st, "Adjustment", new StockLine(1, st.Warehouse, a, MovementType.AdjustmentIn, 0.5m))).Outcome);
        Assert.Equal(-0.5m, (await Balance(st, a)).OnHand);
    }

    [DbFact]
    public async Task AT045_a_receipt_in_cartons_is_kept_in_base_units_with_the_factor_of_the_day()
    {
        Store st = await StoreAsync();
        Guid cola = Guid.CreateVersion7();
        StockResult r = await Post(st, "GoodsReceipt", new StockLine(1, st.Warehouse, cola, MovementType.GoodsReceipt, 2, 4_800_000, new EnteredUnit("CT", 24)));
        Assert.Equal((48m, 100_000L), (r.Movements[0].QtyBase, r.Movements[0].UnitCost));
        StockMovementView m = (await In(st, (s, ct) => Ledger.MovementsAsync(s, cola, st.Warehouse, 10, ct))).Single();
        Assert.Equal((2m, "CT", 24m, 48m), (m.EnteredQty!.Value, m.EnteredUnit!, m.UnitFactor!.Value, m.QtyBase));
    }

    [DbFact]
    public async Task INV004_twenty_issues_racing_for_ten_units_sell_exactly_ten_and_the_ledger_agrees()
    {
        Store st = await StoreAsync();
        Guid v = Guid.CreateVersion7();
        await Post(st, "GoodsReceipt", In(st, v, 10, 10_000));
        using var start = new SemaphoreSlim(0);
        Task<StockResult>[] racers = [.. Enumerable.Range(0, 20).Select(_ => Task.Run(async () => { await start.WaitAsync(); return await Post(st, "Adjustment", Out(st, v, 1)); }))];
        start.Release(20);
        StockResult[] all = await Task.WhenAll(racers);
        Assert.Equal(10, all.Count(r => r.Outcome == StockOutcome.Done));
        Assert.Equal(0m, (await Balance(st, v)).OnHand);
        Assert.Equal(0, await Scalar(st, "SELECT sum(qty_base) FROM inv.stock_movement"));
    }

    [DbFact]
    public async Task Rebuilding_a_balance_from_the_ledger_gives_the_same_cost()
    {
        Store st = await StoreAsync();
        Guid v = Guid.CreateVersion7();
        await Post(st, "GoodsReceipt", In(st, v, 3, 301));
        await Post(st, "Adjustment", Out(st, v, 1));
        await Post(st, "GoodsReceipt", In(st, v, 7, 1_000));
        IReadOnlyList<StockMovementView> ledger = await In(st, (s, ct) => Ledger.MovementsAsync(s, v, null, 100, ct));
        StockState replayed = StockMath.Replay(ledger.Reverse().Select(m => (m.QtyBase, m.TotalCost, StockMath.CarriesCost(Enum.Parse<MovementType>(m.Type)))));
        StockBalanceView b = await Balance(st, v);
        Assert.Equal(new StockState(b.OnHand, b.Wac, b.Value), replayed);
    }
    [DbFact]
    public async Task INV001_the_nightly_oracle_reports_drift_and_the_operator_rebuild_restores_the_balance()
    {
        Store st = await StoreAsync();
        Guid v = Guid.CreateVersion7();
        await Post(st, "GoodsReceipt", In(st, v, 3, 301));
        await Post(st, "Adjustment", Out(st, v, 1));
        StockBalanceView good = await Balance(st, v);

        // a bug (simulated by the superuser, bypassing the ledger) moves the balance away from the ledger
        string admin = new NpgsqlConnectionStringBuilder(App)
        {
            Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres", Password = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "postgres",
        }.ToString();
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var c = new NpgsqlCommand("UPDATE inv.stock_balance SET on_hand = 9, wac = 1, value_on_hand = 9 WHERE tenant_id = $1", conn);
            c.Parameters.Add(new NpgsqlParameter { Value = st.Tenant });
            await c.ExecuteNonQueryAsync();
        }

        string worker = new NpgsqlConnectionStringBuilder(App) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
        await using var wds = NpgsqlDataSource.Create(worker);
        var uow = new PostgresUnitOfWork(wds);
        Assert.Contains(await new OracleRunner(uow).RunAsync(default), f => f.Oracle == "O-2" && f.TenantId == st.Tenant);

        Assert.Equal(1, await new StockMaintenance(uow).RebuildAsync(st.Tenant, default));
        Assert.Equal(good, await Balance(st, v));
        Assert.DoesNotContain(await new OracleRunner(uow).RunAsync(default), f => f.TenantId == st.Tenant);
        Assert.Equal(0, await new StockMaintenance(uow).RebuildAsync(st.Tenant, default));   // nothing left to correct
    }
}
