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

/// <summary>BR-063: consent per purpose and channel; the operational and the marketing kind are separate and never imply each other.</summary>
public sealed record ConsentChoice(string Purpose, string Channel, bool Granted);

public sealed record ConsentView(string Purpose, string Channel, bool Granted, DateTimeOffset UpdatedAt, string PolicyVersion);

public sealed record MembershipView(Guid MembershipId, string StoreName, DateTimeOffset JoinedAt, IReadOnlyList<ConsentView> Consents);

public sealed record MembershipRef(Guid TenantId, Guid MembershipId, string StoreName, DateTimeOffset JoinedAt);

public static class ConsentRules
{
    public static readonly IReadOnlyList<string> Purposes = ["Operational", "Marketing"];

    public static readonly IReadOnlyList<string> Channels = ["Sms", "Bale"];

    /// <summary>The first problem, as <c>field: message</c>, or null (a choice is repeated or names something unknown).</summary>
    public static string? Problem(IReadOnlyList<ConsentChoice>? choices, string? policyVersion)
    {
        if (policyVersion is null || policyVersion.Trim().Length is < 1 or > 20)
        {
            return "policyVersion: 1-20 characters";
        }

        if (choices is null)
        {
            return "consents: required";
        }

        if (choices.Any(c => !Purposes.Contains(c.Purpose) || !Channels.Contains(c.Channel)))
        {
            return "consents: purpose Operational or Marketing, channel Sms or Bale";
        }

        return choices.GroupBy(c => (c.Purpose, c.Channel)).Any(g => g.Count() > 1) ? "consents: a purpose and channel once" : null;
    }
}

/// <summary>CUS-005: the counter QR carries <c>v1.&lt;payload&gt;.&lt;mac&gt;</c>; any change to the payload (store, branch, expiry) breaks the mac (AT-222).</summary>
public static class StoreInviteToken
{
    public sealed record Parsed(Guid TenantId, Guid BranchId, Guid InviteId, DateTimeOffset ExpiresAt);

    public static string Create(IContactProtector protector, Guid tenantId, Guid branchId, Guid inviteId, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(protector);
        string payload = B64(System.Text.Encoding.UTF8.GetBytes($"{tenantId:N}.{branchId:N}.{inviteId:N}.{expiresAt.ToUnixTimeSeconds()}"));
        return $"v1.{payload}.{B64(protector.Hash("store-invite", payload))}";
    }

    public static bool TryRead(IContactProtector protector, string? token, out Parsed parsed)
    {
        ArgumentNullException.ThrowIfNull(protector);
        parsed = null!;
        string[] parts = (token ?? "").Split('.');
        if (parts.Length != 3 || parts[0] != "v1" || parts[1].Length > 200)
        {
            return false;
        }

        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(B64(protector.Hash("store-invite", parts[1]))),
                System.Text.Encoding.ASCII.GetBytes(parts[2])))
        {
            return false;
        }

        string[] f = System.Text.Encoding.UTF8.GetString(FromB64(parts[1])).Split('.');
        if (f.Length != 4 || !Guid.TryParseExact(f[0], "N", out Guid t) || !Guid.TryParseExact(f[1], "N", out Guid b) || !Guid.TryParseExact(f[2], "N", out Guid i)
            || !long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out long exp))
        {
            return false;
        }

        parsed = new Parsed(t, b, i, DateTimeOffset.FromUnixTimeSeconds(exp));
        return true;
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + ((4 - (b.Length % 4)) % 4), '='));
    }
}

public sealed record InviteView(Guid Id, Guid BranchId, string StoreName, string BranchName, DateTimeOffset ExpiresAt);

public interface IStoreInvites
{
    /// <summary>A new invite for a branch of the current store; null when the branch is not one of its own.</summary>
    Task<InviteView?> CreateAsync(IUnitOfWorkScope scope, Guid inviteId, Guid branchId, DateTimeOffset expiresAt, Guid createdBy, CancellationToken cancellationToken);

    Task<bool> RevokeAsync(IUnitOfWorkScope scope, Guid inviteId, CancellationToken cancellationToken);

    /// <summary>The invite when it exists, is not revoked and not expired: with the store's and branch's names, nothing more (AB-11).</summary>
    Task<InviteView?> ActiveAsync(IUnitOfWorkScope scope, Guid inviteId, DateTimeOffset now, CancellationToken cancellationToken);
}

public enum JoinOutcome
{
    Joined,
    AlreadyMember,
}

public interface IMemberships
{
    /// <summary>CUS-003 / DI-062: one membership per customer per store; a customer who left and scans again is reactivated. The consents given are recorded.</summary>
    Task<(JoinOutcome Outcome, MembershipView Membership)> JoinAsync(IUnitOfWorkScope scope, Guid customerId, string source, IReadOnlyList<ConsentChoice> choices,
        string policyVersion, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The customer's stores across all tenants (no tenant context; the directory function).</summary>
    Task<IReadOnlyList<MembershipRef>> ListAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken);

    Task<MembershipView?> GetAsync(IUnitOfWorkScope scope, Guid membershipId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>CUS-007: each change is a new record (history is append-only); the newest per purpose and channel is in force at once.</summary>
    Task<MembershipView?> UpdateConsentsAsync(IUnitOfWorkScope scope, Guid membershipId, Guid customerId, IReadOnlyList<ConsentChoice> choices, string policyVersion,
        string source, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Leaving withdraws every consent and ends the membership (the store keeps its invoices, DI-062).</summary>
    Task<bool> LeaveAsync(IUnitOfWorkScope scope, Guid membershipId, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>CUS-005: the customer's own QR: <c>q1.&lt;jti&gt;.&lt;mac&gt;</c>. It names no one: the jti alone says whose, in the database (opaque).</summary>
public static class CustomerQrToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);

    public static string Create(IContactProtector protector, Guid jti)
    {
        ArgumentNullException.ThrowIfNull(protector);
        string payload = jti.ToString("N");
        return $"q1.{payload}.{Convert.ToBase64String(protector.Hash("customer-qr", payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    public static bool TryRead(IContactProtector protector, string? token, out Guid jti)
    {
        ArgumentNullException.ThrowIfNull(protector);
        jti = Guid.Empty;
        string[] parts = (token ?? "").Split('.');
        if (parts.Length != 3 || parts[0] != "q1" || parts[1].Length != 32)
        {
            return false;
        }

        string mac = Convert.ToBase64String(protector.Hash("customer-qr", parts[1])).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(mac), System.Text.Encoding.ASCII.GetBytes(parts[2]))
            && Guid.TryParseExact(parts[1], "N", out jti);
    }
}

/// <summary>The store's view of a member (BR-061/062): masked by default; the full number only with customers.pii.view.</summary>
public sealed record TenantCustomerView(Guid TenantCustomerId, Guid GlobalCustomerId, string MaskedPhone, DateTimeOffset JoinedAt, IReadOnlyList<ConsentView> Consents);

public interface ICustomerLinks
{
    /// <summary>A QR for the customer, valid 120 seconds, once.</summary>
    Task<(Guid Jti, DateTimeOffset ExpiresAt)> IssueQrAsync(IUnitOfWorkScope scope, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the QR used and returns whose it is; null when unknown, expired or used before (AT-220).</summary>
    Task<Guid?> ConsumeQrAsync(IUnitOfWorkScope scope, Guid jti, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The customer behind a public code, or null (unknown, retired or merged).</summary>
    Task<CustomerView?> FindByCodeAsync(IUnitOfWorkScope scope, string publicCode, CancellationToken cancellationToken);

    Task<CustomerView?> FindByPhoneHashAsync(IUnitOfWorkScope scope, byte[] phoneHash, CancellationToken cancellationToken);

    /// <summary>The customer's number, to send the proof-of-possession code to (only here, only for this).</summary>
    Task<string?> PhoneOfAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken);
}

public interface IStoreMembers
{
    /// <summary>The customer's active membership in the current store (BR-061: only this store).</summary>
    Task<TenantCustomerView?> FindAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken);

    Task<TenantCustomerView?> GetAsync(IUnitOfWorkScope scope, Guid tenantCustomerId, CancellationToken cancellationToken);

    Task<Guid> CreateJoinRequestAsync(IUnitOfWorkScope scope, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The store a still-open request of this customer belongs to (no tenant context).</summary>
    Task<Guid?> JoinRequestTenantAsync(IUnitOfWorkScope scope, Guid requestId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Marks the request of the current store accepted; false when it is not the customer's, expired or already accepted.</summary>
    Task<bool> AcceptJoinRequestAsync(IUnitOfWorkScope scope, Guid requestId, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken);
}
