using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Payments.Application;

/// <summary>D07 <c>Payment</c> as recorded (PAY-001): what was applied, and for cash what was handed over and given back.</summary>
public sealed record PaymentView(Guid Id, string Method, string Status, long Amount, long? Tendered, long? Change, string? ReferenceNo, string? TerminalNo,
    string? CardLast4, DateOnly BusinessDate, Guid? SaleId, Guid? OverrideApprovalId, DateTimeOffset RecordedAt);

/// <summary>Where a payment was taken (SAL-010) and the store's local date it counts for (PAY-002.1).</summary>
public sealed record PaymentOrigin(Guid? SaleId, Guid? RegisterId, Guid? ShiftId, Guid RecordedBy, DateOnly BusinessDate);

public enum PaymentOutcome
{
    Done,
    DuplicateReference,
}

public sealed record PaymentResult(PaymentOutcome Outcome, PaymentView? Payment, Guid? ExistingPaymentId = null);

public interface IPayments
{
    /// <summary>SAL-006.1: cash is confirmed when taken; only <paramref name="amount"/> counts for the bill and the drawer.</summary>
    Task<PaymentView> RecordCashAsync(IUnitOfWorkScope scope, long amount, long tendered, PaymentOrigin origin, CancellationToken cancellationToken);

    /// <summary>
    /// PAY-002: a standalone bank terminal's payment, confirmed by its reference (ConfirmedManual). PAY-002.1: the same
    /// (terminal, reference, day) twice is refused unless <paramref name="overrideApprovalId"/> carries a manager's approval.
    /// </summary>
    Task<PaymentResult> RecordManualCardAsync(IUnitOfWorkScope scope, long amount, string referenceNo, string? terminalNo, string? cardLast4,
        Guid? overrideApprovalId, PaymentOrigin origin, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaymentView>> ForSaleAsync(IUnitOfWorkScope scope, Guid saleId, CancellationToken cancellationToken);
}

/// <summary>PAY-002 input rules and the binding of a manager's override (D-61).</summary>
public static partial class PaymentRules
{
    public static bool ValidReference(string? v) => v is not null && Reference().IsMatch(v);

    public static bool ValidTerminalNo(string? v) => v is null || TerminalNo().IsMatch(v);

    public static bool ValidLast4(string? v) => v is null || Last4().IsMatch(v);

    /// <summary>SAL-006.1: the change handed back from cash.</summary>
    public static long Change(long amount, long tendered) => tendered - amount;

    /// <summary>The override fits this repeated reference on this sale for this amount, and nothing else.</summary>
    public static string OverrideBinding(Guid saleId, string? terminalNo, string referenceNo, DateOnly businessDate, long amount) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{saleId:N}|{terminalNo}|{referenceNo}|{businessDate:yyyy-MM-dd}|{amount}")))[..32];

    [GeneratedRegex("^[0-9A-Za-z]{4,32}$")]
    private static partial Regex Reference();

    [GeneratedRegex("^[0-9A-Za-z]{1,16}$")]
    private static partial Regex TerminalNo();

    [GeneratedRegex("^[0-9]{4}$")]
    private static partial Regex Last4();
}
