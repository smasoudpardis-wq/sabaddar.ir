namespace RetailCloud.BuildingBlocks.Domain;

/// <summary>Single place for monetary rounding rules (BR-013, D-13).</summary>
public static class Rounding
{
    /// <summary>
    /// Rounds to the nearest whole unit; exact halves go away from zero (0.5 → 1, −0.5 → −1),
    /// so a refund of a sale rounds to exactly the negated sale amount.
    /// </summary>
    public static long HalfAwayFromZero(decimal value) =>
        decimal.ToInt64(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
}
