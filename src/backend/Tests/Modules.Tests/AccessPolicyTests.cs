using System.Text.RegularExpressions;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>IAM-006 / D06 §6.1–6.2 / AT-015: role matrix as code, scope coverage, anti-escalation.</summary>
public sealed class AccessPolicyTests
{
    private static readonly Guid B1 = Guid.NewGuid(), B2 = Guid.NewGuid(), W1 = Guid.NewGuid(), R1 = Guid.NewGuid();
    private static readonly string[] Columns =
        ["TenantOwner", "TenantAdmin", "BranchManager", "Cashier", "WarehouseClerk", "PurchasingOfficer", "Accountant", "Auditor", "ReadOnlyAnalyst"];

    private static RoleGrant G(string role, ScopeType t = ScopeType.Tenant, Guid? id = null) => new(role, t, id);

    [Fact]
    public void Role_matrix_in_code_is_exactly_the_D06_table()
    {
        string d06 = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "03-security", "D06-Security-Threat-Model.md"));
        int start = d06.IndexOf("### 6.2", StringComparison.Ordinal);
        var rows = Regex.Matches(d06[start..d06.IndexOf("### 6.3", start, StringComparison.Ordinal)], @"^\| `([a-z.]+)` \|(.*)\|\s*$", RegexOptions.Multiline);
        Assert.Equal(Permissions.All.Count, rows.Count);
        foreach (Match row in rows)
        {
            string permission = row.Groups[1].Value;
            string[] cells = row.Groups[2].Value.Split('|').Select(c => c.Trim()).ToArray();
            Assert.Equal(Columns.Length, cells.Length);
            for (int i = 0; i < Columns.Length; i++)
            {
                GrantReach? expected = cells[i] == "—" ? null : cells[i].StartsWith('✓') ? GrantReach.Tenant : GrantReach.Assigned;
                GrantReach? actual = RoleCatalog.PermissionsOf(Columns[i]).TryGetValue(permission, out GrantReach r) ? r : null;
                Assert.True(expected == actual, $"{Columns[i]} × {permission}: D06 '{cells[i]}' vs code {actual?.ToString() ?? "—"}");
            }
        }
    }

    [Fact]
    public void Permission_catalog_is_the_D02_catalog_without_platform_permissions()
    {
        string d02 = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "00-product", "D02-SRS-Functional-NonFunctional.md"));
        int start = d02.IndexOf("### 3.2", StringComparison.Ordinal);
        var catalog = Regex.Matches(d02[start..d02.IndexOf("## 4.", start, StringComparison.Ordinal)], @"^\| `([a-z.]+)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Where(p => !p.StartsWith("platform.", StringComparison.Ordinal));
        Assert.Equal(catalog.Order(), Permissions.All.Order());
    }

    [Fact]
    public void Role_ids_are_stable_unique_and_reversible()
    {
        Assert.Equal(RoleCatalog.Roles.Count, RoleCatalog.Roles.Select(RoleCatalog.IdOf).Distinct().Count());
        Assert.All(RoleCatalog.Roles, r => Assert.Equal(r, RoleCatalog.FromId(RoleCatalog.IdOf(r))));
        Assert.Null(RoleCatalog.FromId(Guid.NewGuid()));
    }

    [Fact]
    public void Branch_assignment_covers_its_branch_and_its_warehouses_and_registers_only()
    {
        RoleGrant[] manager = [G("BranchManager", ScopeType.Branch, B1)];
        Assert.True(AccessPolicy.Allows(manager, "inventory.adjust", new ScopeRef(ScopeType.Branch, B1)));
        Assert.True(AccessPolicy.Allows(manager, "inventory.adjust", new ScopeRef(ScopeType.Warehouse, W1, B1)));
        Assert.True(AccessPolicy.Allows(manager, "pos.sell", new ScopeRef(ScopeType.Register, R1, B1)));
        Assert.False(AccessPolicy.Allows(manager, "inventory.adjust", new ScopeRef(ScopeType.Branch, B2)));
        Assert.False(AccessPolicy.Allows(manager, "inventory.adjust", new ScopeRef(ScopeType.Warehouse, W1, B2)));
        Assert.False(AccessPolicy.Allows(manager, "inventory.adjust", ScopeRef.Tenant)); // B reach never covers the whole tenant
        Assert.True(AccessPolicy.Allows(manager, "catalog.product.manage", ScopeRef.Tenant)); // ✓ reach: tenant-wide
        Assert.False(AccessPolicy.Allows(manager, "tax.manage", ScopeRef.Tenant));
    }

    [Fact]
    public void Register_assignment_covers_that_register_only()
    {
        RoleGrant[] cashier = [G("Cashier", ScopeType.Register, R1)];
        Assert.True(AccessPolicy.Allows(cashier, "pos.sell", new ScopeRef(ScopeType.Register, R1, B1)));
        Assert.False(AccessPolicy.Allows(cashier, "pos.sell", new ScopeRef(ScopeType.Register, Guid.NewGuid(), B1)));
        Assert.False(AccessPolicy.Allows(cashier, "pos.sell", new ScopeRef(ScopeType.Branch, B1)));
        Assert.False(AccessPolicy.HasAnywhere(cashier, "iam.users.manage")); // SEC-T-012 BFLA
    }

    [Fact]
    public void Nobody_can_assign_power_they_do_not_have()
    {
        RoleGrant[] owner = [G("TenantOwner")], admin = [G("TenantAdmin")], manager = [G("BranchManager", ScopeType.Branch, B1)];
        RoleGrant[] cashier = [G("Cashier", ScopeType.Branch, B1)];
        var b1 = new ScopeRef(ScopeType.Branch, B1);

        Assert.All(RoleCatalog.Roles, r => Assert.True(AccessPolicy.CanAssign(owner, r, RoleCatalog.AllowedScopes(r)[0] == ScopeType.Tenant ? ScopeRef.Tenant : b1), r));
        Assert.False(AccessPolicy.CanAssign(admin, "TenantOwner", ScopeRef.Tenant));
        Assert.False(AccessPolicy.CanAssign(admin, "Accountant", ScopeRef.Tenant)); // admin lacks journal.post / period.close
        Assert.True(AccessPolicy.CanAssign(admin, "BranchManager", b1));

        Assert.True(AccessPolicy.CanAssign(manager, "Cashier", b1));
        Assert.True(AccessPolicy.CanAssign(manager, "Cashier", new ScopeRef(ScopeType.Register, R1, B1)));
        Assert.False(AccessPolicy.CanAssign(manager, "Cashier", new ScopeRef(ScopeType.Branch, B2)));
        Assert.False(AccessPolicy.CanAssign(manager, "Cashier", ScopeRef.Tenant));
        Assert.False(AccessPolicy.CanAssign(manager, "TenantAdmin", ScopeRef.Tenant));
        // clerk permissions are within B1 or the tenant-wide catalog permission the manager also has
        Assert.True(AccessPolicy.CanAssign(manager, "WarehouseClerk", new ScopeRef(ScopeType.Warehouse, W1, B1)));

        Assert.All(RoleCatalog.Roles, r => Assert.False(AccessPolicy.CanAssign(cashier, r, b1), r));
    }

    [Fact]
    public void Roles_can_only_be_assigned_at_their_allowed_scopes()
    {
        RoleGrant[] owner = [G("TenantOwner")];
        Assert.False(AccessPolicy.CanAssign(owner, "Accountant", new ScopeRef(ScopeType.Branch, B1))); // = DB CHECK T97
        Assert.False(AccessPolicy.CanAssign(owner, "Cashier", new ScopeRef(ScopeType.Warehouse, W1, B1)));
        Assert.False(AccessPolicy.CanAssign(owner, "NotARole", ScopeRef.Tenant));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "05-data")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
