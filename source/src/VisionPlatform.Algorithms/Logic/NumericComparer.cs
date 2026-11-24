using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Numerics;

namespace VisionPlatform.Algorithms.Logic;

public enum CompareOperator { GreaterThan, LessThan, Equal, NotEqual, GreaterOrEqual, LessOrEqual }

public sealed record CompareOptions
{
    [DisplayName("比较运算符")]
    public CompareOperator Operator { get; init; } = CompareOperator.GreaterThan;
    [DisplayName("数值 B（无端口输入时）")]
    public double ValueB { get; init; }
    [DisplayName("绝对容差"), Range(0, double.MaxValue)]
    [Description("|A-B|≤容差视为相等；> 要求差值大于容差，≥ 包含相等带。没有相对容差或字符串转换。")]
    public double Tolerance { get; init; } = 1e-6;
}

/// <summary>Compares supported CLR numbers as exact rational values. In particular, Int64/UInt64
/// never pass through Double, and a decimal and binary floating value retain their actual precision.
/// Equality uses an absolute tolerance; strict ordering excludes that equality band.</summary>
public static class NumericComparer
{
    public static IReadOnlyList<string> Validate(CompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Operator)) errors.Add("Operator must be a supported comparison enum value.");
        if (!double.IsFinite(options.ValueB)) errors.Add("The fallback ValueB must be finite.");
        if (!double.IsFinite(options.Tolerance) || options.Tolerance < 0) errors.Add("Tolerance must be finite and nonnegative.");
        return errors;
    }

    public static bool Compare(object valueA, object valueB, CompareOperator operation, double tolerance = 1e-6)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentException("Operator must be a supported comparison enum value.", nameof(operation));
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentException("Tolerance must be finite and nonnegative.", nameof(tolerance));
        var a = Number(valueA, nameof(valueA)); var b = Number(valueB, nameof(valueB));
        var numerator = a.Numerator * b.Denominator - b.Numerator * a.Denominator;
        var denominator = a.Denominator * b.Denominator;
        var band = Binary(tolerance);
        var comparison = (BigInteger.Abs(numerator) * band.Denominator).CompareTo(band.Numerator * denominator);
        var equal = comparison <= 0;
        return operation switch
        {
            CompareOperator.Equal => equal,
            CompareOperator.NotEqual => !equal,
            CompareOperator.GreaterThan => !equal && numerator.Sign > 0,
            CompareOperator.LessThan => !equal && numerator.Sign < 0,
            CompareOperator.GreaterOrEqual => equal || numerator.Sign > 0,
            CompareOperator.LessOrEqual => equal || numerator.Sign < 0,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private readonly record struct Rational(BigInteger Numerator, BigInteger Denominator);
    private static Rational Whole(BigInteger value) => new(value, BigInteger.One);
    private static Rational Number(object? value, string name) => value switch
    {
        byte n => Whole(n), sbyte n => Whole(n), short n => Whole(n), ushort n => Whole(n),
        int n => Whole(n), uint n => Whole(n), long n => Whole(n), ulong n => Whole(n),
        decimal n => Decimal(n),
        float n when float.IsFinite(n) => Binary(n),
        double n when double.IsFinite(n) => Binary(n),
        _ => throw new ArgumentException("Only finite integer, decimal, Single and Double values are accepted; strings and Boolean are not numbers.", name)
    };
    private static Rational Decimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var numerator = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
        if ((bits[3] & int.MinValue) != 0) numerator = -numerator;
        return new(numerator, BigInteger.Pow(10, (bits[3] >> 16) & 0xff));
    }
    private static Rational Binary(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var fraction = bits & ((1UL << 52) - 1);
        var numerator = new BigInteger(exponent == 0 ? fraction : fraction | (1UL << 52));
        if ((bits >> 63) != 0) numerator = -numerator;
        var power = exponent == 0 ? -1074 : exponent - 1023 - 52;
        return power >= 0 ? new(numerator << power, BigInteger.One) : new(numerator, BigInteger.One << -power);
    }
}
