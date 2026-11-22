using System.Security.Cryptography;
using System.Text;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.Platform.Application;

/// <summary>D07 <c>PairingCode</c>. <see cref="Code"/> is shown once to the manager (XXXX-XXXX).</summary>
public sealed record PairingCodeView(string Code, Guid BranchId, DateTimeOffset ExpiresAt);

/// <summary>D07 <c>Terminal</c>. <see cref="DeviceSecret"/> only in the pairing response.</summary>
public sealed record TerminalView(Guid Id, Guid BranchId, string DeviceName, DateTimeOffset PairedAt, string Status, string? DeviceSecret = null);

/// <summary>TEN-002.1 pairing codes: 8 Crockford characters (no I, L, O, U), ~40 bits, 10 minutes, single use.</summary>
public static class PairingCodes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<char> c = stackalloc char[8];
        for (int i = 0; i < c.Length; i++)
        {
            c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{new string(c[..4])}-{new string(c[4..])}";
    }

    /// <summary>Upper-case, dashes and spaces removed, Crockford look-alikes mapped (O→0, I/L→1). Null if malformed.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var sb = new StringBuilder(8);
        foreach (char ch in input.ToUpperInvariant())
        {
            char m = ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch };
            if (m is '-' or ' ')
            {
                continue;
            }

            if (!Alphabet.Contains(m, StringComparison.Ordinal))
            {
                return null;
            }

            sb.Append(m);
        }

        return sb.Length == 8 ? sb.ToString() : null;
    }

    public static byte[] Hash(string normalized) => SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
}

public interface ITerminalAdministration
{
    /// <summary>Null when the branch does not exist in this tenant.</summary>
    Task<PairingCodeView?> IssueCodeAsync(IUnitOfWorkScope scope, Guid branchId, UserId issuedBy, CancellationToken cancellationToken);

    /// <summary>Redeems a code (single use). Null for unknown, used or expired codes.</summary>
    Task<TerminalView?> PairAsync(IUnitOfWorkScope scope, string normalizedCode, string deviceName, UserId pairedBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<TerminalView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task<TerminalView?> GetAsync(IUnitOfWorkScope scope, Guid terminalId, CancellationToken cancellationToken);

    Task RevokeAsync(IUnitOfWorkScope scope, Guid terminalId, UserId revokedBy, CancellationToken cancellationToken);
}
