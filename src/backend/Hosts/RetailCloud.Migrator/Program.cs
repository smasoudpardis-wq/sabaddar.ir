using Npgsql;
using RetailCloud.Migrator;

// US-004 / D05 §11. Usage:
//   RetailCloud.Migrator [--bootstrap] [--dry-run] [--sql-dir <dir>]
// Connection: ConnectionStrings__Migrator (a login that is a member of rc_migrator and rc_tenant_directory;
// --bootstrap needs a superuser once per database: roles, extensions, schemas).
// Exit codes: 0 ok, 1 migration error, 2 fitness violations.
bool bootstrap = args.Contains("--bootstrap"), dryRun = args.Contains("--dry-run");
int dirAt = Array.IndexOf(args, "--sql-dir");
string sqlDir = dirAt >= 0 && dirAt + 1 < args.Length ? args[dirAt + 1] : Path.Combine(AppContext.BaseDirectory, "sql");
string? cs = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("ConnectionStrings__Migrator is not set");
    return 1;
}

await using NpgsqlDataSource ds = NpgsqlDataSource.Create(cs);
try
{
    MigrationReport report = await new MigrationRunner(ds, sqlDir, Console.Out).RunAsync(bootstrap, dryRun, CancellationToken.None);
    Console.WriteLine(dryRun
        ? $"dry run: {report.AlreadyApplied} applied, {report.Pending.Count} pending: {string.Join(", ", report.Pending)}"
        : $"{report.Applied.Count} applied now, {report.AlreadyApplied} already applied");
    foreach (string v in report.FitnessViolations)
    {
        Console.Error.WriteLine($"FITNESS: {v}");
    }

    return report.Ok ? 0 : 2;
}
catch (MigrationException e)
{
    Console.Error.WriteLine($"MIGRATION FAILED: {e.Message}");
    if (Environment.GetEnvironmentVariable("RC_MIGRATOR_DEBUG") == "1") Console.Error.WriteLine(e.InnerException);
    return 1;
}
