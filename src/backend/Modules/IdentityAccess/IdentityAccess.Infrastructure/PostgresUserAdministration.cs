using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.IdentityAccess.Application;

namespace RetailCloud.IdentityAccess.Infrastructure;

public sealed class PostgresUserAdministration(IUnitOfWork unitOfWork, TimeProvider time) : IUserAdministration
{
    /// <summary>Invitation links live 7 days (IAM-001); a new invitation can always be issued.</summary>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public Task<IReadOnlyList<UserView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken) =>
        LoadAsync((PostgresUnitOfWorkScope)scope, null, cancellationToken);

    public async Task<UserView?> GetAsync(IUnitOfWorkScope scope, UserId user, CancellationToken cancellationToken) =>
        (await LoadAsync((PostgresUnitOfWorkScope)scope, user.Value, cancellationToken)).SingleOrDefault();

    public async Task<UserStatusOutcome> SetStatusAsync(IUnitOfWorkScope scope, UserId user, int expectedVersion, bool active,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await s.Transaction.SaveAsync("status", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command(
                "UPDATE iam.tenant_membership SET status = $3, version = version + 1 WHERE user_id = $1 AND version = $2 AND status <> 'Removed'");
            cmd.Parameters.Add(new NpgsqlParameter { Value = user.Value });
            cmd.Parameters.Add(new NpgsqlParameter { Value = expectedVersion });
            cmd.Parameters.Add(new NpgsqlParameter { Value = active ? "Active" : "Suspended" });
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return UserStatusOutcome.Done;
            }
        }
        catch (PostgresException e) when (e.SqlState == "RC010")
        {
            await s.Transaction.RollbackAsync("status", cancellationToken);
            return UserStatusOutcome.LastOwner;
        }

        return await GetAsync(scope, user, cancellationToken) is null ? UserStatusOutcome.NotFound : UserStatusOutcome.VersionConflict;
    }

    private static async Task<IReadOnlyList<UserView>> LoadAsync(PostgresUnitOfWorkScope s, Guid? only, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = s.Command(
            "SELECT u.id, u.display_name, m.status, m.version, a.id, a.role, a.scope_type, a.scope_id " +
            "FROM iam.tenant_membership m JOIN iam.merchant_user u ON u.id = m.user_id " +
            "LEFT JOIN iam.role_assignment a ON a.tenant_id = m.tenant_id AND a.user_id = m.user_id " +
            "WHERE m.status <> 'Removed' AND ($1::uuid IS NULL OR m.user_id = $1) ORDER BY u.display_name, u.id, a.role");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)only ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        var users = new List<(Guid Id, string Name, string Status, int Version, List<AssignmentView> A)>();
        while (await r.ReadAsync(cancellationToken))
        {
            Guid id = r.GetGuid(0);
            if (users.Count == 0 || users[^1].Id != id)
            {
                // D07 User.status: the database's Suspended is the API's Disabled
                users.Add((id, r.GetString(1), r.GetString(2) == "Suspended" ? "Disabled" : r.GetString(2), r.GetInt32(3), []));
            }

            if (!r.IsDBNull(4))
            {
                users[^1].A.Add(View(r.GetGuid(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetGuid(7)));
            }
        }

        return [.. users.Select(u => new UserView(u.Id, u.Name, u.Status, u.A, null, u.Version))];
    }

    public async Task<InvitationView> InviteAsync(IUnitOfWorkScope scope, UserId invitedBy, string displayName, string? email, string? mobile,
        IReadOnlyList<RoleGrant> grants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grants);
        // TEN-003 early check: active members plus open invitations may not exceed the plan's user cap. Accepting is
        // still capped by the database (membership trigger), so a race here can only produce a refused acceptance.
        await using (NpgsqlCommand seats = ((PostgresUnitOfWorkScope)scope).Command(
                         "SELECT platform.entitlement_cap('users'), " +
                         "(SELECT count(*) FROM iam.tenant_membership WHERE status = 'Active') + " +
                         "(SELECT count(*) FROM iam.invitation WHERE status = 'Pending' AND expires_at > now())"))
        await using (NpgsqlDataReader r = await seats.ExecuteReaderAsync(cancellationToken))
        {
            await r.ReadAsync(cancellationToken);
            if (!r.IsDBNull(0) && r.GetInt64(1) >= r.GetInt32(0))
            {
                throw new EntitlementExceededException("users", r.GetInt32(0));
            }
        }

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Guid id = Guid.CreateVersion7();
        DateTimeOffset expires = time.GetUtcNow() + InvitationLifetime;
        string assignments = JsonSerializer.Serialize(grants.Select(g => new { role = g.Role, scopeType = g.ScopeType.ToString(), scopeId = g.ScopeId }));
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO iam.invitation (tenant_id, id, token_hash, display_name, email, mobile, assignments, invited_by, expires_at) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = HashToken(token), NpgsqlDbType = NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = displayName });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)email ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)mobile ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = assignments, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = invitedBy.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = expires });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new InvitationView(id, displayName, "Pending", expires, grants, token);
    }

    public Task<TenantId?> FindInvitationTenantAsync(string token, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteWithoutTenantAsync<TenantId?>(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT iam.invitation_tenant($1)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = HashToken(token), NpgsqlDbType = NpgsqlDbType.Bytea });
            object? v = await cmd.ExecuteScalarAsync(ct);
            return v is Guid g ? new TenantId(g) : null;
        }, cancellationToken);

    public async Task<AcceptedInvitation?> AcceptAsync(IUnitOfWorkScope scope, string token, UserId user, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid invitationId;
        RoleGrant[] grants;
        await using (NpgsqlCommand sel = s.Command(
                         "SELECT id, assignments::text FROM iam.invitation WHERE token_hash = $1 AND status = 'Pending' AND expires_at > now() FOR UPDATE"))
        {
            sel.Parameters.Add(new NpgsqlParameter { Value = HashToken(token), NpgsqlDbType = NpgsqlDbType.Bytea });
            await using NpgsqlDataReader r = await sel.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken))
            {
                return null; // accepted meanwhile, revoked or expired: single use
            }

            invitationId = r.GetGuid(0);
            using JsonDocument doc = JsonDocument.Parse(r.GetString(1));
            grants = [.. doc.RootElement.EnumerateArray().Select(a => new RoleGrant(a.GetProperty("role").GetString()!,
                Enum.Parse<ScopeType>(a.GetProperty("scopeType").GetString()!),
                a.GetProperty("scopeId").ValueKind == JsonValueKind.Null ? null : a.GetProperty("scopeId").GetGuid()))];
        }

        await Exec(s, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (core.current_tenant(), $1) " +
                      "ON CONFLICT (tenant_id, user_id) DO UPDATE SET status = 'Active'", cancellationToken, user.Value);
        foreach (RoleGrant g in grants)
        {
            await Exec(s, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) " +
                          "VALUES (core.current_tenant(), $1, $2, $3, $4, $5) ON CONFLICT DO NOTHING", cancellationToken,
                Guid.CreateVersion7(), user.Value, g.Role, g.ScopeType.ToString(), (object?)g.ScopeId ?? DBNull.Value);
        }

        await Exec(s, "UPDATE iam.invitation SET status = 'Accepted', accepted_by = $2, accepted_at = now() WHERE id = $1",
            cancellationToken, invitationId, user.Value);
        await using NpgsqlCommand name = s.Command("SELECT legal_name FROM platform.tenant");
        return new AcceptedInvitation(scope.TenantId!.Value.Value, (string)(await name.ExecuteScalarAsync(cancellationToken))!, grants);
    }

    public async Task<AssignmentView?> GetAssignmentAsync(IUnitOfWorkScope scope, UserId user, Guid assignmentId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT id, role, scope_type, scope_id FROM iam.role_assignment WHERE user_id = $1 AND id = $2");
        cmd.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = assignmentId });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? View(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3)) : null;
    }

    public async Task<AssignmentView?> AddAssignmentAsync(IUnitOfWorkScope scope, UserId user, RoleGrant grant, UserId assignedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand member = s.Command("SELECT EXISTS (SELECT 1 FROM iam.tenant_membership WHERE user_id = $1 AND status = 'Active')"))
        {
            member.Parameters.Add(new NpgsqlParameter { Value = user.Value });
            if (!(bool)(await member.ExecuteScalarAsync(cancellationToken))!)
            {
                return null;
            }
        }

        Guid id = Guid.CreateVersion7();
        await s.Transaction.SaveAsync("assign", cancellationToken);
        try
        {
            await Exec(s, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id, assigned_by) " +
                          "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6)", cancellationToken,
                id, user.Value, grant.Role, grant.ScopeType.ToString(), (object?)grant.ScopeId ?? DBNull.Value, assignedBy.Value);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await s.Transaction.RollbackAsync("assign", cancellationToken);
            throw new DuplicateAssignmentException();
        }

        return View(id, grant.Role, grant.ScopeType.ToString(), grant.ScopeId);
    }

    public async Task RemoveAssignmentAsync(IUnitOfWorkScope scope, UserId user, Guid assignmentId, CancellationToken cancellationToken)
    {
        try
        {
            await Exec((PostgresUnitOfWorkScope)scope, "DELETE FROM iam.role_assignment WHERE user_id = $1 AND id = $2", cancellationToken, user.Value, assignmentId);
        }
        catch (PostgresException e) when (e.SqlState == "RC010")
        {
            throw new LastOwnerException();
        }
    }

    private static AssignmentView View(Guid id, string role, string scopeType, Guid? scopeId) =>
        new(id, RoleCatalog.IdOf(role), role, scopeType, scopeId);

    private static async Task Exec(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
