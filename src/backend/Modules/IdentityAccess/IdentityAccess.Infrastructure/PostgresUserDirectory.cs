using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.IdentityAccess.Infrastructure;

/// <summary>
/// Resolves IdP identities and the caller's memberships (IAM-001). Memberships come from iam.my_memberships(), which
/// returns only rows of the user set in the transaction (docs/05-data/sql/15_iam.sql, tests T80–T84).
/// </summary>
public sealed class PostgresUserDirectory(IUnitOfWork unitOfWork) : IUserDirectory
{
    public Task<(UserId Id, string DisplayName)?> FindActiveUserAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteWithoutTenantAsync<(UserId, string)?>(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT id, display_name FROM iam.merchant_user WHERE idp_issuer = $1 AND idp_subject = $2 AND status = 'Active'");
            cmd.Parameters.Add(new NpgsqlParameter { Value = issuer });
            cmd.Parameters.Add(new NpgsqlParameter { Value = subject });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? (new UserId(r.GetGuid(0)), r.GetString(1)) : null;
        }, cancellationToken);

    public Task<(UserId Id, string DisplayName)?> EnsureUserAsync(string issuer, string subject, string displayName,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteWithoutTenantAsync<(UserId, string)?>(null, async (s, ct) =>
        {
            // Insert-or-read in one statement; concurrent first logins converge on the same row (UNIQUE issuer+subject).
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "WITH ins AS (INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4) " +
                "ON CONFLICT (idp_issuer, idp_subject) DO NOTHING RETURNING id, display_name, status) " +
                "SELECT id, display_name, status FROM ins UNION ALL " +
                "SELECT id, display_name, status FROM iam.merchant_user WHERE idp_issuer = $2 AND idp_subject = $3 LIMIT 1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = issuer });
            cmd.Parameters.Add(new NpgsqlParameter { Value = subject });
            cmd.Parameters.Add(new NpgsqlParameter { Value = displayName });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) && r.GetString(2) == "Active" ? (new UserId(r.GetGuid(0)), r.GetString(1)) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<MembershipInfo>> ActiveMembershipsAsync(UserId user, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteWithoutTenantAsync<IReadOnlyList<MembershipInfo>>(user, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT tenant_id, tenant_name, assignments::text FROM iam.my_memberships()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<MembershipInfo>();
            while (await r.ReadAsync(ct))
            {
                list.Add(new MembershipInfo(new TenantId(r.GetGuid(0)), r.GetString(1), ParseAssignments(r.GetString(2))));
            }

            return list;
        }, cancellationToken);

    private static RoleGrant[] ParseAssignments(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(a => new RoleGrant(
            a.GetProperty("role").GetString()!,
            Enum.Parse<ScopeType>(a.GetProperty("scopeType").GetString()!),
            a.GetProperty("scopeId").ValueKind == System.Text.Json.JsonValueKind.Null ? null : a.GetProperty("scopeId").GetGuid()))];
    }
}
