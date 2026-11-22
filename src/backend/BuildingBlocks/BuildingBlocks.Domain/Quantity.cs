using System.Globalization;

namespace RetailCloud.BuildingBlocks.Domain;

/// <summary>Decimal quantity with at most 6 fraction digits (BR-012). Unit-specific precision is enforced by the caller.</summary>
public readonly record struct Quantity
{
    public const int MaxScale = 6;
    private const decimal MaxValue = 999_999_999_999m;

    public decimal Value { get; }

    public Quantity(decimal value)
    {
        if (decimal.Abs(value) > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Quantity out of range.");
        }

        if (value.Scale > MaxScale && decimal.Round(value, MaxScale) != value)
        {
            throw new ArgumentException($"Quantity supports at most {MaxScale} fraction digits.", nameof(value));
        }

        Value = decimal.Round(value, MaxScale);
    }

    public static Quantity Zero => new(0m);

    public static Quantity Of(decimal value) => new(value);

    public bool IsPositive => Value > 0;

    /// <summary>Throws when the value has more fraction digits than the unit allows (e.g. "EA" → 0, "KG" → 3).</summary>
    public Quantity EnsurePrecision(int unitDecimals)
    {
        if (decimal.Round(Value, unitDecimals) != Value)
        {
            throw new ArgumentException($"Quantity {Value} exceeds unit precision of {unitDecimals} digits.");
        }

        return this;
    }

    public static Quantity operator +(Quantity left, Quantity right) => new(left.Value + right.Value);

    public static Quantity operator -(Quantity left, Quantity right) => new(left.Value - right.Value);

    public static bool operator <(Quantity left, Quantity right) => left.Value < right.Value;

    public static bool operator >(Quantity left, Quantity right) => left.Value > right.Value;

    public static bool operator <=(Quantity left, Quantity right) => left.Value <= right.Value;

    public static bool operator >=(Quantity left, Quantity right) => left.Value >= right.Value;

    public Quantity Add(Quantity other) => this + other;

    public Quantity Subtract(Quantity other) => this - other;

    public static Quantity FromWireString(string value) =>
        new(decimal.Parse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));

    public string ToWireString() => Value.ToString("0.######", CultureInfo.InvariantCulture);

    public override string ToString() => ToWireString();
}
