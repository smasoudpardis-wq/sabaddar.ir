using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace RetailCloud.Migrator;

public sealed record MigrationStep(string Module, string Id, string File);

public sealed record MigrationManifest(string Bootstrap, string Fitness, IReadOnlyList<MigrationStep> Steps)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MigrationManifest Load(string sqlDirectory)
    {
        string path = Path.Combine(sqlDirectory, "migrations.json");
        MigrationManifest m = JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(path), Json)
            ?? throw new MigrationException($"{path}: empty manifest");
        var seen = new HashSet<(string, string)>();
        foreach (MigrationStep s in m.Steps)
        {
            if (!MigrationRunner.SafeName.IsMatch(s.Module) || !MigrationRunner.SafeId.IsMatch(s.Id))
            {
                throw new MigrationException($"{path}: invalid module or id '{s.Module}/{s.Id}'");
            }

            if (!seen.Add((s.Module, s.Id)))
            {
                throw new MigrationException($"{path}: duplicate step {s.Module}/{s.Id}");
            }
        }

        return m;
    }
}

public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record MigrationReport(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending, int AlreadyApplied, IReadOnlyList<string> FitnessViolations)
{
    public bool Ok => FitnessViolations.Count == 0;
}

/// <summary>
/// D05 §11 migration runner. Steps run in manifest order, each in its own transaction with lock_timeout 3s (retried) and
/// statement_timeout 5min, and are recorded in &lt;module&gt;.migration_history with a checksum. An applied step whose file
/// changed, an applied step missing from the manifest, or a gap (a later step applied before an earlier one) stops the run.
/// One runner at a time (advisory lock). After the run the RLS fitness function must return no rows (NFR-SEC-003).
/// A file whose first line is "-- rc:no-transaction" runs outside a transaction (CREATE INDEX CONCURRENTLY).
/// </summary>
public sealed partial class MigrationRunner(NpgsqlDataSource dataSource, string sqlDirectory, TextWriter? log = null)
{
    internal static readonly Regex SafeName = SafeNameRegex();
    internal static readonly Regex SafeId = SafeIdRegex();
    private const long LockKey = 0x5243_4D49_4752_4154; // "RCMIGRAT"
    private const string NoTransaction = "-- rc:no-transaction";
    private readonly TextWriter _log = log ?? TextWriter.Null;

    public static string Checksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    public async Task<MigrationReport> RunAsync(bool bootstrap, bool dryRun, CancellationToken ct)
    {
        MigrationManifest manifest = MigrationManifest.Load(sqlDirectory);
        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct);
        if (bootstrap)
        {
            _log.WriteLine($"bootstrap {manifest.Bootstrap}");
            await ExecAsync(conn, null, await ReadAsync(manifest.Bootstrap, ct), ct);
        }

        await ExecAsync(conn, null, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            foreach (string module in manifest.Steps.Select(s => s.Module).Distinct())
            {
                await ExecAsync(conn, null,
                    $"SET ROLE rc_migrator; CREATE TABLE IF NOT EXISTS {module}.migration_history (id text PRIMARY KEY, checksum text NOT NULL, " +
                    "applied_at timestamptz NOT NULL DEFAULT now(), applied_by text NOT NULL DEFAULT session_user, duration_ms int NOT NULL); RESET ROLE;", ct);
            }

            Dictionary<(string Module, string Id), string> applied = await AppliedAsync(conn, manifest, ct);
            var known = manifest.Steps.Select(s => (s.Module, s.Id)).ToHashSet();
            foreach ((string module, string id) in applied.Keys.Where(k => !known.Contains(k)))
            {
                throw new MigrationException($"{module}/{id} is applied in the database but missing from the manifest");
            }

            var pending = new List<(MigrationStep Step, string Sql)>();
            bool gap = false;
            foreach (MigrationStep step in manifest.Steps)
            {
                string sql = await ReadAsync(step.File, ct);
                if (applied.TryGetValue((step.Module, step.Id), out string? checksum))
                {
                    if (gap)
                    {
                        throw new MigrationException($"{step.Module}/{step.Id} is applied but an earlier step is not: the database is out of order");
                    }

                    if (checksum != Checksum(sql))
                    {
                        throw new MigrationException($"{step.Module}/{step.Id} ({step.File}) changed after it was applied; add a new step instead");
                    }
                }
                else
                {
                    gap = true;
                    pending.Add((step, sql));
                }
            }

            var done = new List<string>();
            if (!dryRun)
            {
                foreach ((MigrationStep step, string sql) in pending)
                {
                    await ApplyAsync(conn, step, sql, ct);
                    done.Add($"{step.Module}/{step.Id}");
                }
            }

            IReadOnlyList<string> violations = dryRun ? [] : await FitnessAsync(conn, manifest, ct);
            return new MigrationReport(done, [.. pending.Select(p => $"{p.Step.Module}/{p.Step.Id}")], applied.Count, violations);
        }
        finally
        {
            await ExecAsync(conn, null, $"RESET ROLE; SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private async Task ApplyAsync(NpgsqlConnection conn, MigrationStep step, string sql, CancellationToken ct)
    {
        bool inTransaction = !sql.TrimStart().StartsWith(NoTransaction, StringComparison.Ordinal);
        for (int attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            NpgsqlTransaction? tx = inTransaction ? await conn.BeginTransactionAsync(ct) : null;
            try
            {
                if (tx is not null)
                {
                    await ExecAsync(conn, tx, "SET LOCAL lock_timeout = '3s'; SET LOCAL statement_timeout = '5min'", ct);
                }

                await ExecAsync(conn, tx, sql, ct);
                await ExecAsync(conn, tx, "RESET ROLE", ct);
                await using (NpgsqlCommand rec = new($"INSERT INTO {step.Module}.migration_history (id, checksum, duration_ms) VALUES ($1, $2, $3)", conn, tx))
                {
                    rec.Parameters.Add(new NpgsqlParameter { Value = step.Id });
                    rec.Parameters.Add(new NpgsqlParameter { Value = Checksum(sql) });
                    rec.Parameters.Add(new NpgsqlParameter { Value = (int)sw.ElapsedMilliseconds });
                    await rec.ExecuteNonQueryAsync(ct);
                }

                if (tx is not null)
                {
                    await tx.CommitAsync(ct);
                }

                _log.WriteLine($"applied {step.Module}/{step.Id} ({step.File}) in {sw.ElapsedMilliseconds} ms");
                return;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable && tx is not null && attempt < 3)
            {
                await tx.RollbackAsync(ct);
                _log.WriteLine($"lock timeout in {step.Module}/{step.Id}; retry {attempt}");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
            catch (PostgresException e)
            {
                if (tx is not null)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                }

                string at = e.BatchCommand?.CommandText is { } s ? s[..Math.Min(s.Length, 160)].ReplaceLineEndings(" ")
                    : e.Where ?? e.Routine ?? "?";
                throw new MigrationException($"{step.Module}/{step.Id} ({step.File}) failed: {e.SqlState} {e.MessageText} — at: {at}", e);
            }
            finally
            {
                if (tx is not null)
                {
                    await tx.DisposeAsync();
                }

                await ExecAsync(conn, null, "RESET ROLE", CancellationToken.None);
            }
        }
    }

    private static async Task<Dictionary<(string, string), string>> AppliedAsync(NpgsqlConnection conn, MigrationManifest m, CancellationToken ct)
    {
        var applied = new Dictionary<(string, string), string>();
        foreach (string module in m.Steps.Select(s => s.Module).Distinct())
        {
            await using NpgsqlCommand cmd = new($"SELECT id, checksum FROM {module}.migration_history", conn);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                applied[(module, r.GetString(0))] = r.GetString(1);
            }
        }

        return applied;
    }

    private async Task<IReadOnlyList<string>> FitnessAsync(NpgsqlConnection conn, MigrationManifest m, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(await ReadAsync(m.Fitness, ct), conn);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        do
        {
            while (await r.ReadAsync(ct))
            {
                rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "" : r.GetValue(i).ToString())));
            }
        }
        while (await r.NextResultAsync(ct));
        return rows;
    }

    private async Task<string> ReadAsync(string file, CancellationToken ct)
    {
        string full = Path.GetFullPath(Path.Combine(sqlDirectory, file));
        if (!full.StartsWith(Path.GetFullPath(sqlDirectory), StringComparison.Ordinal))
        {
            throw new MigrationException($"{file}: outside the migration directory");
        }

        return await File.ReadAllTextAsync(full, ct);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [GeneratedRegex("^[a-z][a-z_]{0,30}$")]
    private static partial Regex SafeNameRegex();

    [GeneratedRegex("^[0-9]{4}_[a-z0-9_]{1,60}$")]
    private static partial Regex SafeIdRegex();
}
