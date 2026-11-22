using System.Numerics;
using FsCheck.Xunit;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Domain.Tests;

/// <summary>Invariants DI-001/DI-002 and BR-013/BR-014 as properties (D10 §2: property-based Domain tests).</summary>
public class MoneyPropertyTests
{
    private const long Bound = 1_000_000_000_000_000; // 10^15 rials — far beyond any realistic invoice

    private static long Clamp(long v) => v % Bound;

    private static long[] Weights(int[]? raw) =>
        raw is null || raw.Length == 0
            ? [1]
            : raw.Take(50).Select(w => (long)(Math.Abs((long)w) % 1_000_000) + 1).ToArray();

    [Property(MaxTest = 2000)]
    public bool Allocate_parts_always_sum_to_the_total(long total, int[] rawWeights)
    {
        Money amount = Money.Irr(Clamp(total));
        IReadOnlyList<Money> parts = amount.Allocate(Weights(rawWeights));
        return parts.Aggregate(Money.Zero(), (a, b) => a + b) == amount;
    }

    [Property(MaxTest = 2000)]
    public bool Allocate_each_part_is_within_one_rial_of_its_exact_share(long total, int[] rawWeights)
    {
        long t = Clamp(total);
        long[] w = Weights(rawWeights);
        IReadOnlyList<Money> parts = Money.Irr(t).Allocate(w);
        BigInteger sumW = w.Aggregate(BigInteger.Zero, (a, b) => a + b);
        for (int i = 0; i < w.Length; i++)
        {
            // |part × ΣW − total × w| < ΣW  ⇔  |part − exact| < 1
            BigInteger diff = BigInteger.Abs((BigInteger)parts[i].Amount * sumW - (BigInteger)t * w[i]);
            if (diff >= sumW)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 1000)]
    public bool Allocate_is_deterministic_and_sign_symmetric(long total, int[] rawWeights)
    {
        long[] w = Weights(rawWeights);
        Money amount = Money.Irr(Clamp(total));
        var first = amount.Allocate(w);
        var second = amount.Allocate(w);
        var negated = (-amount).Allocate(w);
        return first.SequenceEqual(second) && first.Zip(negated).All(p => p.First == -p.Second);
    }

    [Property(MaxTest = 1000)]
    public bool Addition_is_commutative_associative_and_subtraction_inverts_it(long a, long b, long c)
    {
        Money x = Money.Irr(Clamp(a)), y = Money.Irr(Clamp(b)), z = Money.Irr(Clamp(c));
        return x + y == y + x && (x + y) + z == x + (y + z) && (x + y) - y == x;
    }

    [Property(MaxTest = 2000)]
    public bool Multiply_rounds_exactly_once_half_away_from_zero(long amount, ushort basisPoints)
    {
        long a = Clamp(amount) / 1000; // keep a × factor inside decimal range comfortably
        decimal factor = basisPoints / 10_000m;
        long expected = (long)Math.Round(a * factor, 0, MidpointRounding.AwayFromZero);
        return Money.Irr(a).Multiply(factor).Amount == expected;
    }

    [Property(MaxTest = 2000)]
    public bool Inclusive_tax_split_reconstructs_gross_and_matches_exclusive_tax_within_one_rial(uint gross, ushort rawRate)
    {
        var rate = new TaxRate(rawRate % 10_001);
        Money g = Money.Irr(gross);
        var (net, tax) = rate.SplitInclusive(g);
        bool exact = net + tax == g;
        bool consistent = Math.Abs(rate.TaxOnNet(net).Amount - tax.Amount) <= 1;
        return exact && consistent && !net.IsNegative && !tax.IsNegative;
    }
}
