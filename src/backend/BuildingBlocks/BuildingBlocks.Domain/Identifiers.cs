namespace RetailCloud.BuildingBlocks.Domain;

// Strongly typed identifiers (D03 §9). There is deliberately no Parse(string) on TenantId:
// the active tenant comes only from the authenticated session, never from request input (BR-001).

public readonly record struct TenantId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct BranchId(Guid Value)
{
    public static BranchId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct UserId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public static class Ids
{
    /// <summary>Time-ordered UUIDv7 for new entities (index locality, D05 §1).</summary>
    public static Guid NewId() => Guid.CreateVersion7();
}
