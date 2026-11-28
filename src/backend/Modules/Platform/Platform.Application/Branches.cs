using System.Text.RegularExpressions;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Platform.Application;

/// <summary>D07 <c>Branch</c> / <c>Register</c>.</summary>
public sealed record BranchView(Guid Id, string Code, string Name, string Status);

public sealed record RegisterView(Guid Id, Guid BranchId, string Code);

/// <summary>TEN-002: branches and registers. Codes are short, upper-case and unique (per tenant / per branch).</summary>
public static partial class BranchRules
{
    public const string BranchCreated = "platform.branch-created";

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,9}$")]
    public static partial Regex Code();

    public static Dictionary<string, string[]> Validate(string? code, string? name)
    {
        var errors = new Dictionary<string, string[]>();
        if (code is null || !Code().IsMatch(code))
        {
            errors["code"] = ["format-A-Z0-9-max10"];
        }

        if (name is not null && name.Trim().Length is < 1 or > 100)
        {
            errors["name"] = ["length-1-100"];
        }

        return errors;
    }
}

public sealed class DuplicateCodeException : Exception
{
    public DuplicateCodeException()
        : base("Code already in use.")
    {
    }
}

public interface IBranchAdministration
{
    /// <summary>Branch + its first register R1 in one transaction; emits platform.branch-created (warehouse follows).</summary>
    Task<BranchView> CreateBranchAsync(IUnitOfWorkScope scope, string code, string name, CancellationToken cancellationToken);

    /// <summary>The branch of this tenant (what a receipt prints of it, SAL-012), or null.</summary>
    Task<BranchView?> GetBranchAsync(IUnitOfWorkScope scope, Guid branchId, CancellationToken cancellationToken);

    /// <summary>Null when the branch does not exist in this tenant.</summary>
    Task<RegisterView?> CreateRegisterAsync(IUnitOfWorkScope scope, Guid branchId, string code, CancellationToken cancellationToken);
}
