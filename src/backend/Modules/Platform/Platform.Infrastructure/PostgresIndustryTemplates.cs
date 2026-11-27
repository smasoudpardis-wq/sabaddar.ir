using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

/// <summary>BR-086 on platform.receipt_template_industry (global, append-only: a new text is a new row).</summary>
public sealed class PostgresIndustryTemplates : IIndustryTemplates
{
    private const string Latest =
        "SELECT DISTINCT ON (t.industry_code) t.industry_code, p.name, t.version, t.legal_text, t.locked_fields, t.reason, t.created_by, t.created_at " +
        "FROM platform.receipt_template_industry t JOIN platform.industry_profile p ON p.code = t.industry_code ";

    public async Task<IReadOnlyList<IndustryTemplate>> LatestAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(Latest + "ORDER BY t.industry_code, t.version DESC");
        return await ReadAsync(cmd, cancellationToken);
    }

    public async Task<IndustryTemplate?> LatestAsync(IUnitOfWorkScope scope, string industryCode, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(Latest + "WHERE t.industry_code = $1 ORDER BY t.industry_code, t.version DESC");
        cmd.Parameters.Add(new NpgsqlParameter { Value = industryCode });
        return (await ReadAsync(cmd, cancellationToken)).SingleOrDefault();
    }

    public async Task<IndustryTemplate?> VersionAsync(IUnitOfWorkScope scope, string templateRef, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templateRef);
        string[] parts = templateRef.Split('@');
        if (parts.Length != 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int version))
        {
            return null;
        }

        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(Latest + "WHERE t.industry_code = $1 AND t.version = $2 ORDER BY t.industry_code, t.version DESC");
        cmd.Parameters.Add(new NpgsqlParameter { Value = parts[0] });
        cmd.Parameters.Add(new NpgsqlParameter { Value = version });
        return (await ReadAsync(cmd, cancellationToken)).SingleOrDefault();
    }

    public async Task<(TemplateOutcome Outcome, IndustryTemplate? Template)> PublishAsync(IUnitOfWorkScope scope, string industryCode, int expectedVersion,
        string legalText, IReadOnlyList<string> lockedFields, string reason, string createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lockedFields);
        var s = (PostgresUnitOfWorkScope)scope;
        if (await LatestAsync(scope, industryCode, cancellationToken) is not { } current)
        {
            return (TemplateOutcome.NotFound, null);
        }

        if (current.Version != expectedVersion)
        {
            return (TemplateOutcome.VersionConflict, current);
        }

        await s.Transaction.SaveAsync("template", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command(
                "INSERT INTO platform.receipt_template_industry (industry_code, version, legal_text, locked_fields, reason, created_by) " +
                "VALUES ($1, $2, $3, $4, $5, $6)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = industryCode });
            cmd.Parameters.Add(new NpgsqlParameter { Value = expectedVersion + 1 });
            cmd.Parameters.Add(new NpgsqlParameter { Value = legalText });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lockedFields.ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = reason });
            cmd.Parameters.Add(new NpgsqlParameter { Value = createdBy });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await s.Transaction.ReleaseAsync("template", cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await s.Transaction.RollbackAsync("template", cancellationToken);
            return (TemplateOutcome.VersionConflict, await LatestAsync(scope, industryCode, cancellationToken));   // published meanwhile
        }

        return (TemplateOutcome.Done, await LatestAsync(scope, industryCode, cancellationToken));
    }

    private static async Task<IReadOnlyList<IndustryTemplate>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<IndustryTemplate>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new IndustryTemplate(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetString(5),
                r.GetString(6), r.GetFieldValue<DateTimeOffset>(7)));
        }

        return list;
    }
}
