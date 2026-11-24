using System.Security.Cryptography;
using System.Text;

namespace RetailCloud.BuildingBlocks.Application;

/// <summary>Scope of a role assignment or of a resource (D02 §3.2): Tenant ⊃ Branch ⊃ Warehouse | Register.</summary>
public enum ScopeType
{
    Tenant,
    Branch,
    Warehouse,
    Register,
}

/// <summary>One role assignment of a user in a tenant (iam.role_assignment).</summary>
public sealed record RoleGrant(string Role, ScopeType ScopeType, Guid? ScopeId);

/// <summary>A resource scope; for Warehouse and Register <see cref="BranchId"/> is the branch it belongs to.</summary>
public sealed record ScopeRef(ScopeType Type, Guid? Id = null, Guid? BranchId = null)
{
    public static readonly ScopeRef Tenant = new(ScopeType.Tenant);
}

/// <summary>How far a role's permission reaches (D06 §6.2): ✓ = the whole tenant; B/W/R/OS = only the assignment's scope.</summary>
public enum GrantReach
{
    Tenant,
    Assigned,
}

/// <summary>Permission catalog of D02 §3.2 plus two pseudo-permissions used by endpoint policies.</summary>
public static class Permissions
{
    /// <summary>Any valid identity (sign-up, /me, tenant selection, accepting an invitation).</summary>
    public const string Authenticated = "authenticated";

    /// <summary>Any active member of the active tenant.</summary>
    public const string TenantMember = "tenant.member";

    public const string TenantSettingsManage = "tenant.settings.manage";
    public const string UsersManage = "iam.users.manage";
    public const string TerminalsManage = "terminals.manage";
    public const string AuditView = "audit.view";
    public const string DataExport = "data.export";
    public const string CustomersPiiView = "customers.pii.view";
    public const string CatalogProductManage = "catalog.product.manage";
    public const string CatalogPriceManage = "catalog.price.manage";
    public const string InventoryReceiptCreate = "inventory.receipt.create";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryTransfer = "inventory.transfer";
    public const string InventoryCount = "inventory.count";
    public const string ReportsView = "reports.view";
    public const string ShiftManage = "shift.manage";
    public const string PosSell = "pos.sell";
    public const string PosSaleCancel = "pos.sale.cancel";
    public const string ShiftSupervise = "shift.supervise";
    public const string AccountingRead = "accounting.read";
    public const string AccountingJournalPost = "accounting.journal.post";
    public const string AccountingPeriodClose = "accounting.period.close";
    public const string TemplatesReceiptManage = "templates.receipt.manage";

    public static readonly IReadOnlyList<string> All =
    [
        "tenant.settings.manage", "terminals.manage", "iam.users.manage", "catalog.product.manage", "catalog.price.manage",
        "inventory.receipt.create", "inventory.adjust", "inventory.transfer", "inventory.count", "pos.sell",
        "pos.discount.apply", "pos.sale.cancel", "sales.return.create", "payments.refund", "sales.credit.sell",
        "credit.account.manage", "receivables.collect", "receivables.writeoff", "shift.manage", "shift.supervise",
        "accounting.journal.post", "accounting.period.close", "accounting.read", "customers.lookup", "customers.pii.view",
        "tax.manage", "reports.view", "audit.view", "integrations.manage", "templates.receipt.manage", "data.export",
    ];
}

/// <summary>
/// System roles and their default permissions (D06 §6.2, kept identical by a doc-sync test). System roles cannot be
/// edited; custom roles are a later story. Role ids are stable (derived from the name) so the API can expose roleId.
/// </summary>
public static class RoleCatalog
{
    private const GrantReach T = GrantReach.Tenant, A = GrantReach.Assigned;

    private static readonly Dictionary<string, Dictionary<string, GrantReach>> Matrix = new(StringComparer.Ordinal)
    {
        ["TenantOwner"] = Permissions.All.ToDictionary(p => p, _ => T),
        ["TenantAdmin"] = Permissions.All
            .Except(["receivables.writeoff", "accounting.journal.post", "accounting.period.close", "customers.pii.view"])
            .ToDictionary(p => p, _ => T),
        ["BranchManager"] = new()
        {
            ["terminals.manage"] = A, ["iam.users.manage"] = A, ["catalog.product.manage"] = T, ["catalog.price.manage"] = A, ["inventory.receipt.create"] = A,
            ["inventory.adjust"] = A, ["inventory.transfer"] = A, ["inventory.count"] = A, ["pos.sell"] = A, ["pos.discount.apply"] = A,
            ["pos.sale.cancel"] = A, ["sales.return.create"] = A, ["payments.refund"] = A, ["sales.credit.sell"] = A,
            ["receivables.collect"] = A, ["shift.manage"] = A, ["shift.supervise"] = A, ["accounting.read"] = A,
            ["customers.lookup"] = A, ["reports.view"] = A,
        },
        ["Cashier"] = new()
        {
            ["pos.sell"] = A, ["pos.discount.apply"] = A, ["pos.sale.cancel"] = A, ["sales.return.create"] = A, ["sales.credit.sell"] = A,
            ["receivables.collect"] = A, ["shift.manage"] = A, ["customers.lookup"] = A,
        },
        ["WarehouseClerk"] = new()
        {
            ["catalog.product.manage"] = T, ["inventory.receipt.create"] = A, ["inventory.transfer"] = A, ["inventory.count"] = A,
            ["reports.view"] = A,
        },
        ["PurchasingOfficer"] = new() { ["catalog.product.manage"] = T, ["inventory.receipt.create"] = A, ["reports.view"] = T },
        ["Accountant"] = new()
        {
            ["credit.account.manage"] = T, ["receivables.collect"] = T, ["receivables.writeoff"] = T, ["accounting.journal.post"] = T,
            ["accounting.period.close"] = T, ["accounting.read"] = T, ["customers.lookup"] = T, ["tax.manage"] = T, ["reports.view"] = T,
            ["data.export"] = T,
        },
        ["Auditor"] = new() { ["accounting.read"] = T, ["reports.view"] = T, ["audit.view"] = T, ["data.export"] = T },
        ["ReadOnlyAnalyst"] = new() { ["accounting.read"] = T, ["reports.view"] = T },
    };

    private static readonly Dictionary<string, ScopeType[]> Scopes = new(StringComparer.Ordinal)
    {
        ["TenantOwner"] = [ScopeType.Tenant],
        ["TenantAdmin"] = [ScopeType.Tenant],
        ["Accountant"] = [ScopeType.Tenant],
        ["Auditor"] = [ScopeType.Tenant],
        ["ReadOnlyAnalyst"] = [ScopeType.Tenant],
        ["BranchManager"] = [ScopeType.Tenant, ScopeType.Branch],
        ["Cashier"] = [ScopeType.Tenant, ScopeType.Branch, ScopeType.Register],
        ["WarehouseClerk"] = [ScopeType.Tenant, ScopeType.Branch, ScopeType.Warehouse],
        ["PurchasingOfficer"] = [ScopeType.Tenant, ScopeType.Branch, ScopeType.Warehouse],
    };

    /// <summary>
    /// Role × permission pairs held only under a limit (D06 §6.2 cells "سقف" / "+Approval"): beyond it the action needs
    /// an approval (IAM-007), and such a role can never be the approver for that action.
    /// </summary>
    private static readonly HashSet<(string Role, string Permission)> Capped =
    [
        ("Cashier", "pos.discount.apply"), ("Cashier", "sales.return.create"), ("Accountant", "receivables.writeoff"),
    ];

    public static bool IsCapped(string role, string permission) => Capped.Contains((role, permission));

    public static IReadOnlyCollection<(string Role, string Permission)> CappedPairs => Capped;

    public static IReadOnlyList<string> Roles { get; } = [.. Matrix.Keys];

    public static bool Exists(string role) => Matrix.ContainsKey(role);

    public static IReadOnlyDictionary<string, GrantReach> PermissionsOf(string role) => Matrix[role];

    public static IReadOnlyList<ScopeType> AllowedScopes(string role) => Scopes[role];

    /// <summary>Stable id of a system role (UUID v8 from SHA-256 of the name).</summary>
    public static Guid IdOf(string role)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("rc-system-role\n" + role));
        h[6] = (byte)((h[6] & 0x0F) | 0x80);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    public static string? FromId(Guid id) => Matrix.Keys.FirstOrDefault(r => IdOf(r) == id);
}

/// <summary>
/// D06 §6.1 steps 3–6: role → permission → scope coverage. ABAC conditions (amount limits, own shift, MFA freshness)
/// are checked by the handlers and filters that own the data.
/// </summary>
public static class AccessPolicy
{
    /// <summary>True if any assignment grants the permission for some scope (endpoint gate before data is known).</summary>
    public static bool HasAnywhere(IEnumerable<RoleGrant> assignments, string permission) =>
        assignments.Any(a => RoleCatalog.Exists(a.Role) && RoleCatalog.PermissionsOf(a.Role).ContainsKey(permission));

    /// <summary>True if an assignment grants the permission and its reach covers the resource scope.</summary>
    public static bool Allows(IEnumerable<RoleGrant> assignments, string permission, ScopeRef resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        foreach (RoleGrant a in assignments)
        {
            if (!RoleCatalog.Exists(a.Role) || !RoleCatalog.PermissionsOf(a.Role).TryGetValue(permission, out GrantReach reach))
            {
                continue;
            }

            if (reach == GrantReach.Tenant || Covers(a, resource))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Does the assignment's own scope contain the resource?</summary>
    public static bool Covers(RoleGrant assignment, ScopeRef resource)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(resource);
        return assignment.ScopeType switch
        {
            ScopeType.Tenant => true,
            ScopeType.Branch => (resource.Type == ScopeType.Branch && resource.Id == assignment.ScopeId)
                                || (resource.Type is ScopeType.Warehouse or ScopeType.Register && resource.BranchId == assignment.ScopeId),
            _ => resource.Type == assignment.ScopeType && resource.Id == assignment.ScopeId,
        };
    }

    /// <summary>
    /// IAM-007 / D06 §6.1 step 7: an approver holds the action's permission over the resource scope through a role that
    /// is NOT capped for it (a cashier cannot approve another cashier's discount). Requester ≠ approver is checked by
    /// the caller and by the database (SoD-01).
    /// </summary>
    public static bool CanApprove(IEnumerable<RoleGrant> approver, string action, ScopeRef scope) =>
        approver.Where(a => RoleCatalog.Exists(a.Role) && !RoleCatalog.IsCapped(a.Role, action))
            .Any(a => Allows([a], action, scope));

    /// <summary>
    /// Anti-escalation: the grantor may assign <paramref name="role"/> at <paramref name="scope"/> only if the scope is
    /// allowed for the role, the grantor manages users there, and the grantor holds every permission of the role over
    /// the same reach (nobody can hand out power they do not have).
    /// </summary>
    public static bool CanAssign(IEnumerable<RoleGrant> grantor, string role, ScopeRef scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!RoleCatalog.Exists(role) || !RoleCatalog.AllowedScopes(role).Contains(scope.Type))
        {
            return false;
        }

        RoleGrant[] mine = [.. grantor];
        if (!Allows(mine, Permissions.UsersManage, scope))
        {
            return false;
        }

        return RoleCatalog.PermissionsOf(role).All(p => Allows(mine, p.Key, p.Value == GrantReach.Tenant ? ScopeRef.Tenant : scope));
    }
}
