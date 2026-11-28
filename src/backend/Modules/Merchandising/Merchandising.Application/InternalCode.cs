namespace RetailCloud.Merchandising.Application;

/// <summary>CAT-003: a store's own code (not GS1): letters, digits and '-', up to 32; it never leaves the tenant.</summary>
public static class InternalCode
{
    public static bool IsValid(string? code) =>
        code is { Length: >= 1 and <= 32 } && code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');
}
