using Microsoft.EntityFrameworkCore;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

// Two independent module contexts over the D05 reference schema (platform and inv), as Sales/Inventory/Accounting
// contexts will be in production. Each maps only its own schema (rule R5).

public sealed class Branch
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Warehouse
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public string Code { get; set; } = "";
    public string Kind { get; set; } = "Sellable";
}

public sealed class PlatformDb(DbContextOptions<PlatformDb> options) : DbContext(options)
{
    public DbSet<Branch> Branches => Set<Branch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Branch>(e =>
        {
            e.ToTable("branch", "platform");
            e.HasKey(x => new { x.TenantId, x.Id });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
}

public sealed class InventoryDb(DbContextOptions<InventoryDb> options) : DbContext(options)
{
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Warehouse>(e =>
        {
            e.ToTable("warehouse", "inv");
            e.HasKey(x => new { x.TenantId, x.Id });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Kind).HasColumnName("kind");
        });
}
