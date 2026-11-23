using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

/// <summary>
/// UC-10 step "ساخت خودکار": one transaction in the new tenant's context (the id comes from the idempotency layer).
/// Only Platform data is written here; other modules provision themselves from the event (D03 §6.1: tenant setup is
/// not one of the five atomic multi-module operations).
/// </summary>
public sealed class PostgresTenantRegistration(IOutbox outbox, TimeProvider time) : ITenantRegistration
{
    public async Task<TenantProfile> RegisterAsync(IUnitOfWorkScope scope, RegisterTenant command, UserId owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Guid tenantId = scope.TenantId?.Value ?? throw new InvalidOperationException("Registration needs the new tenant's context.");
        var s = (PostgresUnitOfWorkScope)scope;
        string timeZone = command.TimeZone ?? TenantRegistrationRules.DefaultTimeZone;
        string pricingMode = command.PricingMode ?? "TaxExclusive";
        Guid branchId = Guid.CreateVersion7(), registerId = Guid.CreateVersion7();

        await Exec(s, "INSERT INTO platform.tenant (id, legal_name, industry_code, time_zone, pricing_mode) VALUES ($1, $2, $3, $4, $5)",
            cancellationToken, tenantId, command.LegalName.Trim(), command.IndustryCode, timeZone, pricingMode);
        await Exec(s, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', $3)",
            cancellationToken, tenantId, branchId, command.FirstBranchName.Trim());
        await Exec(s, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1')",
            cancellationToken, tenantId, registerId, branchId);

        await outbox.AddAsync(scope, "Platform", PlatformEvents.TenantRegistered, 1, tenantId, new
        {
            tenantId,
            ownerUserId = owner.Value,
            legalName = command.LegalName.Trim(),
            industryCode = command.IndustryCode,
            timeZone,
            pricingMode,
            firstBranchId = branchId,
            firstRegisterId = registerId,
            registeredAt = time.GetUtcNow(),
        }, causationId: null, cancellationToken);

        // the insert trigger started the trial subscription (TEN-003); read the profile back with it
        return await PostgresTenantProfileQuery.ReadAsync(s, cancellationToken)
            ?? throw new InvalidOperationException("The new tenant is not visible in its own context.");
    }

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

/// <summary>Setup step: first branch and register exist (written synchronously at registration).</summary>
public sealed class BranchAndRegisterSetupStep : ITenantSetupStep
{
    public string Name => "branch-and-register";

    public async Task<bool> IsCompleteAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT EXISTS (SELECT 1 FROM platform.branch) AND EXISTS (SELECT 1 FROM platform.register)");
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }
}

public sealed class BranchScopeLookup : IScopeLookup
{
    public ScopeType Type => ScopeType.Branch;

    public async Task<Guid?> BranchOfAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT id FROM platform.branch WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        return await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
    }
}

public sealed class RegisterScopeLookup : IScopeLookup
{
    public ScopeType Type => ScopeType.Register;

    public async Task<Guid?> BranchOfAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT branch_id FROM platform.register WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        return await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
    }
}

/// <summary>TEN-002 (US-012).</summary>
public sealed class PostgresBranchAdministration(IOutbox outbox) : IBranchAdministration
{
    public async Task<BranchView> CreateBranchAsync(IUnitOfWorkScope scope, string code, string name, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid branchId = Guid.CreateVersion7(), registerId = Guid.CreateVersion7();
        await Insert(s, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES (core.current_tenant(), $1, $2, $3)",
            cancellationToken, branchId, code, name.Trim());
        await Insert(s, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES (core.current_tenant(), $1, $2, 'R1')",
            cancellationToken, registerId, branchId);
        await outbox.AddAsync(scope, "Platform", BranchRules.BranchCreated, 1, branchId,
            new { branchId, code, name = name.Trim(), firstRegisterId = registerId }, causationId: null, cancellationToken);
        return new BranchView(branchId, code, name.Trim(), "Active");
    }

    public async Task<RegisterView?> CreateRegisterAsync(IUnitOfWorkScope scope, Guid branchId, string code, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand exists = s.Command("SELECT EXISTS (SELECT 1 FROM platform.branch WHERE id = $1)"))
        {
            exists.Parameters.Add(new NpgsqlParameter { Value = branchId });
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                return null;
            }
        }

        Guid id = Guid.CreateVersion7();
        await Insert(s, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES (core.current_tenant(), $1, $2, $3)",
            cancellationToken, id, branchId, code);
        return new RegisterView(id, branchId, code);
    }

    /// <summary>Unique violations become <see cref="DuplicateCodeException"/> without aborting the transaction.</summary>
    private static async Task Insert(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await s.Transaction.SaveAsync("code", ct);
        try
        {
            await using NpgsqlCommand cmd = s.Command(sql);
            foreach (object a in args)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = a });
            }

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await s.Transaction.RollbackAsync("code", ct);
            throw new DuplicateCodeException();
        }
    }
}

