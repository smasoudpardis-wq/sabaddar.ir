using System.Globalization;
using System.Numerics;

namespace RetailCloud.BuildingBlocks.Domain;

/// <summary>ISO-4217 currencies supported by the platform. MVP: IRR only (ADR-009, A-03).</summary>
public enum Currency
{
    IRR = 364,
}

/// <summary>
/// Monetary amount in the currency's smallest stored unit. For IRR that is the rial (no minor unit, A-22).
/// Never uses floating point (BR-010). Multiplication by a rate is explicit and rounded (BR-013).
/// Division is intentionally absent: use <see cref="Allocate"/> to split an amount without losing a rial.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public long Amount { get; }
    public Currency Currency { get; }

    public Money(long amount, Currency currency)
    {
        if (!Enum.IsDefined(currency))
        {
            throw new ArgumentOutOfRangeException(nameof(currency), currency, "Unsupported currency.");
        }

        Amount = amount;
        Currency = currency;
    }

    public static Money Irr(long rials) => new(rials, Currency.IRR);

    public static Money Zero(Currency currency = Currency.IRR) => new(0, currency);

    public bool IsZero => Amount == 0;

    public bool IsNegative => Amount < 0;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Amount + right.Amount), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Amount - right.Amount), left.Currency);
    }

    public static Money operator -(Money value) => new(checked(-value.Amount), value.Currency);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    public Money Negate() => -this;

    /// <summary>Multiplies by an exact decimal factor and rounds once, half away from zero (BR-013, D-13).</summary>
    public Money Multiply(decimal factor) => new(Rounding.HalfAwayFromZero(Amount * factor), Currency);

    /// <summary>Multiplies by a quantity (e.g. unit price × qty) and rounds once.</summary>
    public Money Multiply(Quantity quantity) => Multiply(quantity.Value);

    /// <summary>
    /// Splits this amount proportionally to <paramref name="weights"/> using the largest-remainder method (BR-014):
    /// the parts always sum exactly to this amount, each part differs from its exact share by less than one unit,
    /// and ties are broken by position so the result is deterministic.
    /// </summary>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<long> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0)
        {
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        }

        if (weights.Any(w => w < 0))
        {
            throw new ArgumentException("Weights must be non-negative.", nameof(weights));
        }

        BigInteger total = weights.Aggregate(BigInteger.Zero, (acc, w) => acc + w);
        if (total.IsZero)
        {
            throw new ArgumentException("The sum of weights must be positive.", nameof(weights));
        }

        // Allocate the absolute value, then restore the sign, so refunds mirror sales exactly.
        int sign = Math.Sign(Amount);
        BigInteger amount = BigInteger.Abs(Amount);
        var shares = new long[weights.Count];
        var remainders = new BigInteger[weights.Count];
        BigInteger allocated = BigInteger.Zero;
        for (int i = 0; i < weights.Count; i++)
        {
            BigInteger product = amount * weights[i];
            BigInteger share = BigInteger.DivRem(product, total, out BigInteger remainder);
            shares[i] = (long)share;
            remainders[i] = remainder;
            allocated += share;
        }

        long leftover = (long)(amount - allocated);
        foreach (int index in Enumerable.Range(0, weights.Count)
                     .OrderByDescending(i => remainders[i])
                     .ThenBy(i => i)
                     .Take((int)leftover))
        {
            shares[index]++;
        }

        Currency currency = Currency;
        return shares.Select(s => new Money(sign * s, currency)).ToArray();
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Stable wire format: integer amount as an invariant string (D07 <c>Money</c> schema).</summary>
    public string ToWireString() => Amount.ToString(CultureInfo.InvariantCulture);

    public static Money FromWireString(string value, Currency currency = Currency.IRR)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long amount))
        {
            throw new FormatException("Money must be an integer string in the smallest unit.");
        }

        return new Money(amount, currency);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount} {Currency}");

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Currency mismatch: {left.Currency} vs {right.Currency}.");
        }
    }
}
