namespace RetailCloud.Accounting.Application;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

/// <summary>One row of a default chart. Non-postable rows are headings; posting goes to leaves only.</summary>
public sealed record AccountTemplate(string Code, string Name, AccountType Type, string? ParentCode, bool Postable,
    bool RequiresDetail = false, string? Role = null);

/// <summary>
/// Default chart of accounts per industry (TEN-001.1) with the account-role mapping of D-30/D-59. Codes follow the usual
/// Iranian group/ledger layout (1 assets … 5 expenses). Pending the client accountant's review (X-04, OQ-05): the
/// template is versioned so later tenants can get a revised chart without touching existing ones.
/// </summary>
public static class ChartOfAccountsTemplate
{
    public const int Version = 1;

    /// <summary>Every role a posting rule may reference (= CHECK of acc.account_role in 50_acc.sql).</summary>
    public static readonly IReadOnlyList<string> Roles =
    [
        "SalesRevenue", "OutputVat", "Inventory", "COGS", "CashOnHand", "Bank", "GatewayInTransit", "CardInTransit",
        "TradeReceivables", "CustomerAdvances", "TradePayables", "InputVat", "InventoryShrinkage", "InventoryOverage",
        "CashShortage", "CashOverage", "BadDebtExpense", "InventoryCostAdjustment", "OpeningBalanceEquity", "BankFees",
        "PurchaseReturnVariance",
    ];

    private static readonly AccountTemplate[] Retail =
    [
        new("1", "دارایی‌ها", AccountType.Asset, null, false),
        new("11", "دارایی‌های جاری", AccountType.Asset, "1", false),
        new("1101", "صندوق", AccountType.Asset, "11", true, Role: "CashOnHand"),
        new("1102", "بانک", AccountType.Asset, "11", true, Role: "Bank"),
        new("1103", "وجوه درگاه پرداخت در راه", AccountType.Asset, "11", true, Role: "GatewayInTransit"),
        new("1104", "وجوه کارتخوان در راه", AccountType.Asset, "11", true, Role: "CardInTransit"),
        new("1105", "حساب‌های دریافتنی تجاری", AccountType.Asset, "11", true, RequiresDetail: true, Role: "TradeReceivables"),
        new("1106", "موجودی کالا", AccountType.Asset, "11", true, Role: "Inventory"),
        new("1107", "مالیات و عوارض ارزش افزوده خرید", AccountType.Asset, "11", true, Role: "InputVat"),
        new("2", "بدهی‌ها", AccountType.Liability, null, false),
        new("21", "بدهی‌های جاری", AccountType.Liability, "2", false),
        new("2101", "حساب‌های پرداختنی تجاری", AccountType.Liability, "21", true, RequiresDetail: true, Role: "TradePayables"),
        new("2102", "پیش‌دریافت از مشتریان", AccountType.Liability, "21", true, RequiresDetail: true, Role: "CustomerAdvances"),
        new("2103", "مالیات و عوارض ارزش افزوده فروش", AccountType.Liability, "21", true, Role: "OutputVat"),
        new("3", "حقوق مالکانه", AccountType.Equity, null, false),
        new("3101", "سرمایه", AccountType.Equity, "3", true),
        new("3102", "تراز افتتاحیه", AccountType.Equity, "3", true, Role: "OpeningBalanceEquity"),
        new("4", "درآمدها", AccountType.Revenue, null, false),
        new("4101", "فروش کالا", AccountType.Revenue, "4", true, Role: "SalesRevenue"),
        new("4102", "اضافی انبار", AccountType.Revenue, "4", true, Role: "InventoryOverage"),
        new("4103", "اضافی صندوق", AccountType.Revenue, "4", true, Role: "CashOverage"),
        new("5", "هزینه‌ها", AccountType.Expense, null, false),
        new("5101", "بهای تمام‌شده کالای فروش‌رفته", AccountType.Expense, "5", true, Role: "COGS"),
        new("5102", "کسری انبار", AccountType.Expense, "5", true, Role: "InventoryShrinkage"),
        new("5103", "کسری صندوق", AccountType.Expense, "5", true, Role: "CashShortage"),
        new("5104", "هزینه مطالبات مشکوک‌الوصول", AccountType.Expense, "5", true, Role: "BadDebtExpense"),
        new("5105", "تعدیل بهای موجودی", AccountType.Expense, "5", true, Role: "InventoryCostAdjustment"),
        new("5106", "کارمزد بانکی", AccountType.Expense, "5", true, Role: "BankFees"),
        new("5107", "اختلاف بهای برگشت از خرید", AccountType.Expense, "5", true, Role: "PurchaseReturnVariance"),
    ];

    /// <summary>MVP: one retail chart for every industry; industry-specific charts plug in here (SQ-03 style).</summary>
    public static IReadOnlyList<AccountTemplate> For(string industryCode) => industryCode switch
    {
        "supermarket" or "apparel" or "general" => Retail,
        _ => throw new ArgumentOutOfRangeException(nameof(industryCode), industryCode, "Unknown industry"),
    };
}
