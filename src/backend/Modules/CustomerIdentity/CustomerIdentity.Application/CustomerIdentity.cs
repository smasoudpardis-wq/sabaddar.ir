using System.Globalization;
using System.Security.Cryptography;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.CustomerIdentity.Application;

/// <summary>CUS-001 / TM-043: the limits of the phone check. Numbers are the SRS's; the per-address cap is the recommended default (REC).</summary>
public static class OtpPolicy
{
    public const int Digits = 6;
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(2);
    public const int MaxAttempts = 5;
    public static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(60);
    public const int PerPhonePerHour = 5;
    public const int PerPhonePerDay = 10;
    public const int PerAddressPerHour = 30;
}

/// <summary>Iranian mobile numbers only (TM-043: no foreign toll numbers): <c>+98 9XX XXX XXXX</c>, written in any usual way and digits.</summary>
public static class PhoneNumber
{
    public static bool TryNormalize(string? input, out string e164)
    {
        e164 = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var digits = new System.Text.StringBuilder();
        foreach (char c in input.Trim())
        {
            if (c is >= '۰' and <= '۹') digits.Append((char)('0' + (c - '۰')));
            else if (c is >= '٠' and <= '٩') digits.Append((char)('0' + (c - '٠')));
            else if (c is >= '0' and <= '9') digits.Append(c);
            else if (c is ' ' or '-' or '(' or ')' || (c == '+' && digits.Length == 0)) continue;
            else return false;
        }

        string d = digits.ToString();
        if (d.StartsWith("0098", StringComparison.Ordinal)) d = d[4..];
        else if (d.StartsWith("98", StringComparison.Ordinal) && d.Length == 12) d = d[2..];
        else if (d.StartsWith('0') && d.Length == 11) d = d[1..];
        if (d.Length != 10 || d[0] != '9')
        {
            return false;
        }

        e164 = "+98" + d;
        return true;
    }

    /// <summary>The number as a customer may see it back: <c>0912***4567</c>.</summary>
    public static string Mask(string e164) => e164.Length == 13 ? "0" + e164[3..6] + "***" + e164[9..] : "***";
}

public static class OtpCode
{
    public static string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
}

/// <summary>CUS-002: <c>CU-</c> and eight Crockford Base32 characters (no I, L, O, U) from a CSPRNG (~40 bits).</summary>
public static class PublicCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        Span<char> c = stackalloc char[8];
        for (int i = 0; i < c.Length; i++)
        {
            c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return "CU-" + new string(c);
    }

    public static bool IsValid(string? code) =>
        code is { Length: 11 } && code.StartsWith("CU-", StringComparison.Ordinal) && code[3..].All(ch => Alphabet.Contains(ch, StringComparison.Ordinal));
}

/// <summary>DI-064: the phone is kept encrypted and found by a keyed hash (blind index); the code and addresses only as keyed hashes.</summary>
public interface IContactProtector
{
    byte[] Hash(string purpose, string value);

    byte[] Protect(string value);

    string Unprotect(byte[] protectedValue);
}

public enum OtpStartOutcome
{
    Started,
    TooSoon,
    PhoneLimit,
    AddressLimit,
}

public sealed record OtpStart(OtpStartOutcome Outcome, Guid ChallengeId, DateTimeOffset ExpiresAt, DateTimeOffset ResendAfter, TimeSpan RetryAfter);

public sealed record OtpChallengeState(Guid Id, byte[] PhoneHash, byte[] CodeHash, byte[] ProtectedPhone, int Attempts, string Status, DateTimeOffset ExpiresAt);

public interface IOtpChallenges
{
    /// <summary>Checks the limits and, when allowed, stores the challenge in one step (a concurrent burst cannot slip past the counts).</summary>
    Task<OtpStart> StartAsync(IUnitOfWorkScope scope, byte[] phoneHash, byte[] ipHash, byte[] codeHash, byte[] protectedPhone, DateTimeOffset now, CancellationToken cancellationToken);

    Task<OtpChallengeState?> LockAsync(IUnitOfWorkScope scope, Guid challengeId, CancellationToken cancellationToken);

    /// <summary>One more wrong try; the fifth ends the challenge. Returns the tries left.</summary>
    Task<int> FailAttemptAsync(IUnitOfWorkScope scope, Guid challengeId, CancellationToken cancellationToken);

    Task VerifiedAsync(IUnitOfWorkScope scope, Guid challengeId, CancellationToken cancellationToken);
}

public sealed record CustomerView(Guid Id, string PublicCode, bool IsNew, string MaskedPhone);

public interface ICustomers
{
    /// <summary>The customer who owns this verified number, or a new one with a fresh public code (DI-060: one active customer per number).</summary>
    Task<CustomerView> FindOrCreateAsync(IUnitOfWorkScope scope, string e164, DateTimeOffset now, CancellationToken cancellationToken);

    Task<CustomerView?> GetAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken);

    /// <summary>CUS-002.1: the old code is invalid at once and is never issued again (DI-061).</summary>
    Task<CustomerView?> RotateCodeAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken);
}

/// <summary>Sends the code to the phone. It is the only place the number and the code meet; neither is logged.</summary>
public interface IOtpSender
{
    Task<bool> SendAsync(string e164, string code, string idempotencyKey, CancellationToken cancellationToken);
}
