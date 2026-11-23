namespace RetailCloud.Accounting.Application;

/// <summary>A line of a posting rule: an account role (D-30), mapped to the tenant's account when posted.</summary>
public sealed record RoleLine(string Role, long Debit, long Credit);

/// <summary>What a posting rule produces for one business event (ACC-004); the version is stored on the entry.</summary>
public sealed record PostingDraft(string SourceType, Guid SourceId, string Purpose, int RuleVersion, DateTimeOffset EffectiveAt, string Description,
    IReadOnlyList<RoleLine> Lines);

/// <summary>
/// D02 §5.8.1 «مغایرت صندوق»: a shortage debits CashShortage and credits CashOnHand; an overage debits CashOnHand and
/// credits CashOverage; no variance, no entry. Version 1 (the accountant's review is OQ-05).
/// </summary>
public static class CashVarianceRule
{
    public const int Version = 1;

    public static PostingDraft? For(Guid shiftId, long variance, DateTimeOffset closedAt) => variance switch
    {
        0 => null,
        < 0 => new PostingDraft("Shift", shiftId, "cash-variance", Version, closedAt, "کسری صندوق",
            [new RoleLine("CashShortage", -variance, 0), new RoleLine("CashOnHand", 0, -variance)]),
        _ => new PostingDraft("Shift", shiftId, "cash-variance", Version, closedAt, "اضافه صندوق",
            [new RoleLine("CashOnHand", variance, 0), new RoleLine("CashOverage", 0, variance)]),
    };
}
