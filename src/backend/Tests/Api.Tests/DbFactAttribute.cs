namespace RetailCloud.Api.Tests;

/// <summary>Needs PostgreSQL with the D05 schema (scripts/setup-test-db.sh). Skipped only when RC_TEST_PG is unset; CI sets it.</summary>
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
