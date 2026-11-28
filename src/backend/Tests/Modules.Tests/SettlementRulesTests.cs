using RetailCloud.Accounting.Application;
using RetailCloud.Payments.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-105 / PAY-002.2: reading the bank's settlement file, matching it to the terminal's payments, and J4.</summary>
public sealed class SettlementRulesTests
{
    private static readonly DateOnly Day = new(2026, 11, 24);

    [Fact]
    public void A_csv_with_English_headers_reads_its_rows_and_uppercases_references()
    {
        (IReadOnlyList<SettlementLine> lines, IReadOnlyList<SettlementRowError> errors) = SettlementCsv.Parse(
            "﻿terminal_no,reference_no,transaction_date,amount,fee\r\n8899,ab123456,2026-11-24,600000,3000\r\n\r\n,99887766,2026-11-25,\"1,200,000\",\n");
        Assert.Empty(errors);
        Assert.Equal(2, lines.Count);
        Assert.Equal(new SettlementLine(1, "8899", "AB123456", Day, 600_000, 3000), lines[0]);
        Assert.Equal(new SettlementLine(2, null, "99887766", Day.AddDays(1), 1_200_000, 0), lines[1]);
    }

    [Fact]
    public void Persian_headers_digits_separators_and_Jalali_dates_are_read()
    {
        (IReadOnlyList<SettlementLine> lines, IReadOnlyList<SettlementRowError> errors) = SettlementCsv.Parse(
            "شماره ترمینال;شماره پیگیری;تاریخ تراکنش;مبلغ;کارمزد\n۸۸۹۹;۱۲۳۴۵۶۷۸;۱۴۰۵/۰۹/۰۳;۶۰۰٬۰۰۰;۳٬۰۰۰\n");
        Assert.Empty(errors);
        Assert.Equal(new SettlementLine(1, "8899", "12345678", Day, 600_000, 3000), Assert.Single(lines));
    }

    [Fact]
    public void A_file_with_any_bad_row_is_refused_whole_and_says_where()
    {
        (IReadOnlyList<SettlementLine> lines, IReadOnlyList<SettlementRowError> errors) = SettlementCsv.Parse(
            "reference,date,amount,fee\n12345678,2026-11-24,1000,2000\n123,2026-13-01,-5,\n12345678,2026-11-24,1000,0\n");
        Assert.Empty(lines);
        Assert.Equal(new[] { (2, "fee"), (3, "reference"), (3, "date"), (3, "amount") }, errors.Select(e => (e.Line, e.Field)));
    }

    [Theory]
    [InlineData("terminal,date,amount\n1,2026-11-24,5\n", "reference")]
    [InlineData("reference,amount\nABCD1234,5\n", "date")]
    [InlineData("\n\n", "file")]
    [InlineData("reference,date,amount\n", "file")]
    public void Missing_columns_or_rows_are_reported(string csv, string field) => Assert.Contains(SettlementCsv.Parse(csv).Errors, e => e.Field == field);

    [Fact]
    public void No_more_than_5000_rows_are_taken()
    {
        string csv = "reference,date,amount\n" + string.Concat(Enumerable.Range(0, SettlementCsv.MaxLines + 1).Select(i => $"REF{i:D6},2026-11-24,1000\n"));
        Assert.Equal("file", Assert.Single(SettlementCsv.Parse(csv).Errors).Field);
    }

    private static SettlementLine L(int no, string reference, long amount, DateOnly? date = null, string? terminal = null, long fee = 0) =>
        new(no, terminal, reference, date ?? Day, amount, fee);

    private static SettlementCandidate C(string reference, long amount, DateOnly? date = null, string? terminal = "8899", bool settled = false, Guid? sale = null) =>
        new(Guid.CreateVersion7(), reference, terminal, date ?? Day, amount, sale ?? Sale, settled);

    private static readonly Guid Sale = Guid.CreateVersion7();
    private static readonly HashSet<Guid> Finalized = [Sale];

    [Fact]
    public void PAY002_2_a_line_settles_the_one_payment_with_its_reference_and_amount()
    {
        SettlementCandidate p = C("AB123456", 600_000);
        SettlementMatch m = Assert.Single(SettlementMatcher.Match([L(1, "ab123456", 600_000, terminal: "8899")], [p], Finalized));
        Assert.Equal((SettlementOutcome.Matched, p.PaymentId), (m.Outcome, m.PaymentId));
    }

    [Fact]
    public void Every_other_case_is_left_for_the_accountant_with_why()
    {
        SettlementCandidate open = C("R0000001", 500), settled = C("R0000002", 700, settled: true), twinA = C("R0000003", 900), twinB = C("R0000003", 900),
            far = C("R0000004", 100, date: Day.AddDays(-3)), otherTerminal = C("R0000005", 100, terminal: "1111"), unbooked = C("R0000006", 100, sale: Guid.CreateVersion7());
        SettlementCandidate[] all = [open, settled, twinA, twinB, far, otherTerminal, unbooked];
        IReadOnlyList<SettlementMatch> m = SettlementMatcher.Match(
        [
            L(1, "R0000001", 499), L(2, "R0000002", 700), L(3, "R0000003", 900), L(4, "R0000004", 100), L(5, "R0000005", 100, terminal: "8899"),
            L(6, "R0000006", 100), L(7, "R0000009", 100),
        ], all, Finalized);
        Assert.Equal(
            new[] { SettlementOutcome.AmountMismatch, SettlementOutcome.AlreadySettled, SettlementOutcome.Ambiguous, SettlementOutcome.NotFound, SettlementOutcome.NotFound,
                SettlementOutcome.Unfinalized, SettlementOutcome.NotFound },
            m.Select(x => x.Outcome));
        Assert.Equal(open.PaymentId, m[0].PaymentId);      // the payment it resembles, for the accountant to look at
        Assert.Equal(unbooked.PaymentId, m[5].PaymentId);
    }

    [Fact]
    public void The_same_slip_twice_in_one_file_settles_once_and_twins_are_told_apart_by_the_exact_day()
    {
        SettlementCandidate today = C("R0000003", 900), yesterday = C("R0000003", 900, date: Day.AddDays(-1));
        IReadOnlyList<SettlementMatch> m = SettlementMatcher.Match([L(1, "R0000003", 900), L(2, "R0000003", 900), L(3, "R0000003", 900)], [today, yesterday], Finalized);
        Assert.Equal((SettlementOutcome.Matched, today.PaymentId), (m[0].Outcome, m[0].PaymentId));
        Assert.Equal((SettlementOutcome.Matched, yesterday.PaymentId), (m[1].Outcome, m[1].PaymentId));
        Assert.Equal(SettlementOutcome.DuplicateInFile, m[2].Outcome);
        Assert.Equal((2, 1800L, 0L), SettlementMatcher.Totals(m));
    }

    [Fact]
    public void J4_moves_the_gross_out_of_CardInTransit_into_Bank_less_the_bank_fees()
    {
        Guid import = Guid.CreateVersion7();
        PostingDraft d = CardSettlementRule.For(import, 600_000, 3000, DateTimeOffset.UnixEpoch, "تسویه")!;
        Assert.Equal(("SettlementImport", import, "card-settlement"), (d.SourceType, d.SourceId, d.Purpose));
        Assert.Equal(new[] { new RoleLine("Bank", 597_000, 0), new RoleLine("BankFees", 3000, 0), new RoleLine("CardInTransit", 0, 600_000) }, d.Lines);
        Assert.Equal(d.Lines.Sum(l => l.Debit), d.Lines.Sum(l => l.Credit));
        Assert.Null(CardSettlementRule.For(import, 0, 0, DateTimeOffset.UnixEpoch, "x"));
        Assert.Equal(new[] { "Bank", "CardInTransit" }, CardSettlementRule.For(import, 10, 0, DateTimeOffset.UnixEpoch, "x")!.Lines.Select(l => l.Role));
        Assert.Throws<ArgumentOutOfRangeException>(() => CardSettlementRule.For(import, 10, 11, DateTimeOffset.UnixEpoch, "x"));
    }
}
