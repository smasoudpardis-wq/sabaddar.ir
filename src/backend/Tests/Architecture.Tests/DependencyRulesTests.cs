using System.Reflection;
using NetArchTest.Rules;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Architecture.Tests;

/// <summary>D04 §5.2 dependency rules R1–R11 as executable tests (NFR-MNT-001). New modules add their assemblies here.</summary>
public class DependencyRulesTests
{
    private static readonly Assembly Domain = typeof(Money).Assembly;
    private static readonly Assembly Application = typeof(IUnitOfWork).Assembly;
    private static readonly Assembly Infrastructure = typeof(PostgresUnitOfWork).Assembly;

    // Module Application layers (one per module as they are added)
    private static readonly Assembly[] ModuleApplications =
    [
        typeof(RetailCloud.IdentityAccess.Application.IdentityAccessModule).Assembly,
        typeof(RetailCloud.Platform.Application.ITenantProfileQuery).Assembly,
        typeof(RetailCloud.Inventory.Application.InventoryModule).Assembly,
        typeof(RetailCloud.Accounting.Application.ChartOfAccountsTemplate).Assembly,
        typeof(RetailCloud.Merchandising.Application.IProductCatalog).Assembly,
        typeof(RetailCloud.Sales.Application.IShifts).Assembly,
        typeof(RetailCloud.Payments.Application.IPayments).Assembly,
        typeof(RetailCloud.Notifications.Application.INotifications).Assembly,
    ];

    private static readonly Assembly[] ModuleInfrastructures =
    [
        typeof(RetailCloud.IdentityAccess.Infrastructure.PostgresUserDirectory).Assembly,
        typeof(RetailCloud.Platform.Infrastructure.PostgresTenantProfileQuery).Assembly,
        typeof(RetailCloud.Inventory.Infrastructure.DefaultWarehouseConsumer).Assembly,
        typeof(RetailCloud.Accounting.Infrastructure.TenantAccountingSetupConsumer).Assembly,
        typeof(RetailCloud.Merchandising.Infrastructure.PostgresProductCatalog).Assembly,
        typeof(RetailCloud.Sales.Infrastructure.PostgresShifts).Assembly,
        typeof(RetailCloud.Payments.Infrastructure.PostgresPayments).Assembly,
        typeof(RetailCloud.Notifications.Infrastructure.NotificationDispatcher).Assembly,
    ];

    private static readonly string[] InfrastructureNamespaces =
    [
        "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "System.Net.Http", "System.Data",
    ];

    private static void AssertSuccess(TestResult result, string rule) =>
        Assert.True(result.IsSuccessful,
            $"{rule} violated by: {string.Join(", ", result.FailingTypeNames ?? [])}");

    [Fact]
    public void R1_domain_depends_on_nothing_infrastructural() =>
        AssertSuccess(Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, "RetailCloud.BuildingBlocks.Application", "RetailCloud.BuildingBlocks.Infrastructure"])
            .GetResult(), "R1");

    [Fact]
    public void R2_application_does_not_depend_on_infrastructure_or_data_access() =>
        AssertSuccess(Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, "RetailCloud.BuildingBlocks.Infrastructure"])
            .GetResult(), "R2");

    [Fact]
    public void R2_module_application_layers_do_not_depend_on_infrastructure_or_web() =>
        AssertSuccess(Types.InAssemblies(ModuleApplications).ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, "RetailCloud.BuildingBlocks.Infrastructure", "RetailCloud.BuildingBlocks.Api"])
            .GetResult(), "R2 (modules)");

    [Fact]
    public void R3_modules_do_not_reference_each_others_infrastructure()
    {
        foreach (Assembly a in ModuleInfrastructures)
        {
            string own = a.GetName().Name!.Replace(".Infrastructure", "", StringComparison.Ordinal);
            string[] others = ModuleInfrastructures.Concat(ModuleApplications)
                .Select(x => x.GetName().Name!)
                .Where(n => !n.StartsWith(own, StringComparison.Ordinal))
                .ToArray();
            AssertSuccess(Types.InAssembly(a).ShouldNot().HaveDependencyOnAny(others).GetResult(), $"R3 ({own})");
        }
    }

    [Fact]
    public void R8_application_and_domain_never_use_HttpClient() =>
        AssertSuccess(Types.InAssemblies([Domain, Application]).ShouldNot()
            .HaveDependencyOn("System.Net.Http").GetResult(), "R8");

    [Fact]
    public void Domain_has_no_floating_point_members_BR010()
    {
        var offenders = Domain.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => (t, n: f.Name, ty: f.FieldType))
                .Concat(t.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(p => (t, n: p.Name, ty: p.PropertyType))))
            .Where(x => x.ty == typeof(double) || x.ty == typeof(float))
            .Select(x => $"{x.t.Name}.{x.n}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Domain_value_objects_are_immutable()
    {
        // No public setter (init-only is allowed: it cannot change after construction) and no writable instance field.
        var offenders = Domain.GetTypes()
            .Where(t => !t.IsEnum && !t.IsInterface && t != typeof(SystemClock) && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
            .SelectMany(t =>
                t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.SetMethod is { IsPublic: true } set &&
                                !set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
                    .Select(p => $"{t.Name}.{p.Name} (setter)")
                    .Concat(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(f => !f.IsInitOnly)
                        .Select(f => $"{t.Name}.{f.Name} (field)")))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void R9_only_BuildingBlocks_Infrastructure_sets_tenant_context()
    {
        string root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains("BuildingBlocks.Infrastructure", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("app.tenant_id", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void R11_only_the_Migrator_changes_the_schema()
    {
        // D05 §11.1: Api and Workers never run migrations. They do not reference the Migrator, and no other source touches
        // the migration history or runs DDL through code.
        string root = FindRepoRoot();
        string backend = Path.Combine(root, "src", "backend");
        foreach (string host in new[] { "RetailCloud.Api", "RetailCloud.Workers" })
        {
            string csproj = File.ReadAllText(Path.Combine(backend, "Hosts", host, host + ".csproj"));
            Assert.DoesNotContain("RetailCloud.Migrator", csproj, StringComparison.Ordinal);
        }

        string[] forbidden = ["migration_history", "pg_advisory_lock(", "CREATE TABLE", "ALTER TABLE", "DROP TABLE"];
        var offenders = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains("RetailCloud.Migrator", StringComparison.Ordinal))
            .Where(f => forbidden.Any(x => File.ReadAllText(f).Contains(x, StringComparison.OrdinalIgnoreCase)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void R10_only_BuildingBlocks_Infrastructure_writes_outbox_and_inbox_directly()
    {
        // Modules emit through IOutbox (envelope, prefix and tenant rules) and consume through IIntegrationEventConsumer.
        string root = FindRepoRoot();
        string[] forbidden = ["INSERT INTO core.outbox", "UPDATE core.outbox", "INSERT INTO core.inbox"];
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains("BuildingBlocks.Infrastructure", StringComparison.Ordinal))
            .Where(f => forbidden.Any(x => File.ReadAllText(f).Contains(x, StringComparison.OrdinalIgnoreCase)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "05-data")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
