using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using RetailCloud.Migrator;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>US-004 / D05 §11 / SEC-T-061: the runner applies each step once, in order, atomically, and refuses drift.</summary>
public sealed class MigrationRunnerTests : IAsyncLifetime
{
    private static readonly string Admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused")
    {
        Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
        Password = Environment.GetEnvironmentVariable("PGPASSWORD"),
        Database = "postgres",
    }.ToString();

    private readonly string _db = "rc_mig_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-mig-" + Guid.NewGuid().ToString("N"));
    private NpgsqlDataSource? _ds;

    private NpgsqlDataSource Ds => _ds!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        // a private copy of the real migration set, so tests can append or edit steps
        Directory.CreateDirectory(_dir);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "docs", "05-data", "sql")))
        {
            File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)));
        }

        await using (var admin = NpgsqlDataSource.Create(Admin))
        {
            await using NpgsqlCommand c = admin.CreateCommand($"CREATE DATABASE {_db}");
            await c.ExecuteNonQueryAsync();
        }

        _ds = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Admin) { Database = _db, Pooling = false }.ToString());
    }

    public async Task DisposeAsync()
    {
        if (_ds is null)
        {
            return;
        }

        await _ds.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Admin);
        await using NpgsqlCommand c = admin.CreateCommand($"DROP DATABASE IF EXISTS {_db} WITH (FORCE)");
        await c.ExecuteNonQueryAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "docs", "05-data")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private Task<MigrationReport> Run(bool bootstrap = false) => new MigrationRunner(Ds, _dir).RunAsync(bootstrap, dryRun: false, CancellationToken.None);

    private void AppendStep(string module, string id, string sql)
    {
        string file = $"{module}_{id}.sql";
        File.WriteAllText(Path.Combine(_dir, file), sql);
        string path = Path.Combine(_dir, "migrations.json");
        JsonNode m = JsonNode.Parse(File.ReadAllText(path))!;
        m["steps"]!.AsArray().Add(new JsonObject { ["module"] = module, ["id"] = id, ["file"] = file });
        File.WriteAllText(path, m.ToJsonString());
    }

    private async Task<long> Scalar(string sql)
    {
        await using NpgsqlCommand c = Ds.CreateCommand(sql);
        return Convert.ToInt64(await c.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [DbFact]
    public async Task Fresh_database_gets_every_step_once_and_passes_fitness()
    {
        MigrationReport first = await Run(bootstrap: true);
        Assert.Equal(12, first.Applied.Count);
        Assert.True(first.Ok, string.Join("; ", first.FitnessViolations));
        MigrationReport second = await Run();
        Assert.Empty(second.Applied);
        Assert.Equal(12, second.AlreadyApplied);
        Assert.Equal(2, await Scalar("SELECT count(*) FROM core.migration_history"));
    }

    [DbFact]
    public async Task Only_the_new_step_runs_after_an_upgrade()
    {
        await Run(bootstrap: true);
        AppendStep("platform", "0002_branch_note", "SET ROLE rc_migrator; ALTER TABLE platform.branch ADD COLUMN note text; RESET ROLE;");
        MigrationReport r = await Run();
        Assert.Equal(["platform/0002_branch_note"], r.Applied);
        Assert.Equal(1, await Scalar("SELECT count(*) FROM information_schema.columns WHERE table_schema='platform' AND table_name='branch' AND column_name='note'"));
    }

    [DbFact]
    public async Task A_failing_step_leaves_nothing_behind_and_is_not_recorded()
    {
        await Run(bootstrap: true);
        AppendStep("inv", "0002_broken", "SET ROLE rc_migrator; CREATE TABLE inv.half_done (id int); SELECT no_such_function();");
        MigrationException e = await Assert.ThrowsAsync<MigrationException>(() => Run());
        Assert.Contains("inv/0002_broken", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, await Scalar("SELECT count(*) FROM pg_tables WHERE schemaname='inv' AND tablename='half_done'"));
        Assert.Equal(0, await Scalar("SELECT count(*) FROM inv.migration_history WHERE id='0002_broken'"));
        Assert.Equal(0, await Scalar("SELECT count(*) FROM pg_roles r WHERE r.rolname = current_user AND current_user <> session_user"));   // role reset
    }

    [DbFact]
    public async Task A_step_and_its_history_row_commit_together()
    {
        // the step succeeds but recording it fails (it dropped its own history table): the step must roll back as well,
        // otherwise the next run would apply it a second time
        await Run(bootstrap: true);
        AppendStep("inv", "0002_drops_history", "SET ROLE rc_migrator; DROP TABLE inv.migration_history; RESET ROLE;");
        await Assert.ThrowsAsync<MigrationException>(() => Run());
        Assert.Equal(1, await Scalar("SELECT count(*) FROM inv.migration_history WHERE id = '0001_baseline'"));
    }

    [DbFact]
    public async Task An_applied_step_that_was_edited_stops_the_run()
    {
        await Run(bootstrap: true);
        File.AppendAllText(Path.Combine(_dir, "20_merch.sql"), "\n-- edited after release\n");
        MigrationException e = await Assert.ThrowsAsync<MigrationException>(() => Run());
        Assert.Contains("merch/0001_baseline", e.Message, StringComparison.Ordinal);
        Assert.Contains("changed after it was applied", e.Message, StringComparison.Ordinal);
    }

    [DbFact]
    public async Task A_step_applied_but_missing_from_the_manifest_stops_the_run()
    {
        await Run(bootstrap: true);
        string path = Path.Combine(_dir, "migrations.json");
        JsonNode m = JsonNode.Parse(File.ReadAllText(path))!;
        m["steps"]!.AsArray().RemoveAt(11);   // core/0002_oracles
        File.WriteAllText(path, m.ToJsonString());
        MigrationException e = await Assert.ThrowsAsync<MigrationException>(() => Run());
        Assert.Contains("core/0002_oracles", e.Message, StringComparison.Ordinal);
    }

    [DbFact]
    public async Task A_tenant_table_without_RLS_fails_the_fitness_check()
    {
        await Run(bootstrap: true);
        AppendStep("sales", "0002_no_rls", "SET ROLE rc_migrator; CREATE TABLE sales.forgot_rls (tenant_id uuid NOT NULL, x int); RESET ROLE;");
        MigrationReport r = await Run();
        Assert.False(r.Ok);
        Assert.Contains(r.FitnessViolations, v => v.Contains("missing-rls|sales.forgot_rls", StringComparison.Ordinal));
    }

    [DbFact]
    public async Task Concurrent_runners_apply_each_step_exactly_once()
    {
        await new MigrationRunner(Ds, _dir).RunAsync(bootstrap: true, dryRun: true, CancellationToken.None);   // bootstrap only
        MigrationReport[] reports = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Run()));
        Assert.Equal(12, reports.Sum(r => r.Applied.Count));
        Assert.All(reports, r => Assert.True(r.Ok));
    }
}
