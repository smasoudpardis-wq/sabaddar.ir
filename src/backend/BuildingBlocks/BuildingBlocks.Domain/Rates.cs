namespace RetailCloud.BuildingBlocks.Domain;

/// <summary>A ratio in basis points (9% = 900). Integers only — no floating point (BR-010).</summary>
public readonly record struct Percentage
{
    public int BasisPoints { get; }

    public Percentage(int basisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(basisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(basisPoints, 10_000);
        BasisPoints = basisPoints;
    }

    public decimal AsFactor => BasisPoints / 10_000m;

    /// <summary>The rounded portion of <paramref name="amount"/> (e.g. a percentage discount).</summary>
    public Money Of(Money amount) => amount.Multiply(AsFactor);
}

/// <summary>Versioned tax rate snapshot value (BR-017). Applies BR-013 (exclusive) and BR-016 (inclusive).</summary>
public readonly record struct TaxRate
{
    public int BasisPoints { get; }

    public TaxRate(int basisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(basisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(basisPoints, 10_000);
        BasisPoints = basisPoints;
    }

    public static TaxRate None => new(0);

    public decimal AsFactor => BasisPoints / 10_000m;

    /// <summary>Tax on a tax-exclusive net amount: round(net × rate).</summary>
    public Money TaxOnNet(Money net) => net.Multiply(AsFactor);

    /// <summary>
    /// Splits a tax-inclusive amount: net = round(gross ÷ (1 + rate)), tax = gross − net (BR-016).
    /// net + tax always equals gross exactly.
    /// </summary>
    public (Money Net, Money Tax) SplitInclusive(Money gross)
    {
        long net = Rounding.HalfAwayFromZero(gross.Amount / (1m + AsFactor));
        var netMoney = new Money(net, gross.Currency);
        return (netMoney, gross - netMoney);
    }
}
