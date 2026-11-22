namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// Integration test that needs PostgreSQL with the D05 schema (scripts/setup-test-db.sh).
/// Skipped locally when RC_TEST_PG is not set; CI always sets it, so these never silently skip there.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RC_TEST_PG")))
        {
            Skip = "RC_TEST_PG not set — run scripts/setup-test-db.sh and export RC_TEST_PG";
        }
    }
}
