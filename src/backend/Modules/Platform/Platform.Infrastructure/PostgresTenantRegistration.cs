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

        return new TenantProfile(tenantId, command.LegalName.Trim(), command.IndustryCode, timeZone, "IRR", pricingMode, "Trial", 1);
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
