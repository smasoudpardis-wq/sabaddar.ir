using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

/// <summary>TEN-002.1 (US-013): pairing codes and terminals; only hashes of codes and device secrets are stored.</summary>
public sealed class PostgresTerminals(IUnitOfWork unitOfWork, TimeProvider time) : ITerminalAdministration, ITerminalDirectory
{
    public static byte[] HashSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public async Task<PairingCodeView?> IssueCodeAsync(IUnitOfWorkScope scope, Guid branchId, UserId issuedBy, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand exists = s.Command("SELECT EXISTS (SELECT 1 FROM platform.branch WHERE id = $1 AND status = 'Active')"))
        {
            exists.Parameters.Add(new NpgsqlParameter { Value = branchId });
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                return null;
            }
        }

        string code = PairingCodes.New();
        DateTimeOffset expires = time.GetUtcNow() + PairingCodes.Lifetime;
        await using NpgsqlCommand cmd = s.Command(
            "INSERT INTO platform.pairing_code (tenant_id, id, branch_id, code_hash, issued_by, expires_at) VALUES (core.current_tenant(), $1, $2, $3, $4, $5)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = branchId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = PairingCodes.Hash(PairingCodes.Normalize(code)!), NpgsqlDbType = NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = issuedBy.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = expires });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new PairingCodeView(code, branchId, expires);
    }

    public async Task<TerminalView?> PairAsync(IUnitOfWorkScope scope, string normalizedCode, string deviceName, UserId pairedBy, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid branchId;
        await using (NpgsqlCommand claim = s.Command(
                         "UPDATE platform.pairing_code SET used_at = now() WHERE code_hash = $1 AND used_at IS NULL AND expires_at > now() RETURNING branch_id"))
        {
            claim.Parameters.Add(new NpgsqlParameter { Value = PairingCodes.Hash(normalizedCode), NpgsqlDbType = NpgsqlDbType.Bytea });
            if (await claim.ExecuteScalarAsync(cancellationToken) is not Guid b)
            {
                return null;
            }

            branchId = b;
        }

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Guid id = Guid.CreateVersion7();
        await using (NpgsqlCommand ins = s.Command(
                         "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES (core.current_tenant(), $1, $2, $3, $4, $5) RETURNING paired_at"))
        {
            ins.Parameters.Add(new NpgsqlParameter { Value = id });
            ins.Parameters.Add(new NpgsqlParameter { Value = branchId });
            ins.Parameters.Add(new NpgsqlParameter { Value = deviceName.Trim() });
            ins.Parameters.Add(new NpgsqlParameter { Value = HashSecret(secret), NpgsqlDbType = NpgsqlDbType.Bytea });
            ins.Parameters.Add(new NpgsqlParameter { Value = pairedBy.Value });
            DateTimeOffset pairedAt = (DateTimeOffset)(await ins.ExecuteScalarAsync(cancellationToken) is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : time.GetUtcNow());
            return new TerminalView(id, branchId, deviceName.Trim(), pairedAt, "Active", secret);
        }
    }

    public async Task<IReadOnlyList<TerminalView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT id, branch_id, device_name, paired_at, status FROM platform.terminal ORDER BY status, device_name");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<TerminalView>();
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new TerminalView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4)));
        }

        return list;
    }

    public async Task<TerminalView?> GetAsync(IUnitOfWorkScope scope, Guid terminalId, CancellationToken cancellationToken) =>
        (await ListAsync(scope, cancellationToken)).FirstOrDefault(t => t.Id == terminalId);

    public async Task RevokeAsync(IUnitOfWorkScope scope, Guid terminalId, UserId revokedBy, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE platform.terminal SET status = 'Revoked', revoked_at = now(), revoked_by = $2 WHERE id = $1 AND status = 'Active'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = terminalId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = revokedBy.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<TerminalInfo?> FindActiveAsync(TenantId tenant, string deviceSecret, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<TerminalInfo?>(new FixedTenantContext(tenant), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT id, branch_id FROM platform.terminal WHERE secret_hash = $1 AND status = 'Active'");
            cmd.Parameters.Add(new NpgsqlParameter { Value = HashSecret(deviceSecret), NpgsqlDbType = NpgsqlDbType.Bytea });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? new TerminalInfo(r.GetGuid(0), r.GetGuid(1)) : null;
        }, cancellationToken);
}
