using System.Globalization;
using System.Numerics;

namespace KingbaseConnector.UnitTests;

public sealed class NumericTests
{
    [Theory]
    [InlineData("0", "0", 0, "0")]
    [InlineData("-0.00", "0", 2, "0.00")]
    [InlineData("+00123.4500", "1234500", 4, "123.4500")]
    [InlineData("-.0001", "-1", 4, "-0.0001")]
    [InlineData("1.2300e2", "12300", 2, "123.00")]
    [InlineData("123e-5", "123", 5, "0.00123")]
    [InlineData("1.2e3", "1200", 0, "1200")]
    [InlineData("5.", "5", 0, "5")]
    [InlineData("0e1234567", "0", 0, "0")]
    public void ParsePreservesFiniteCoefficientAndScale(string text, string coefficient, int scale, string formatted)
    {
        var number = KingbaseNumeric.Parse(text);
        Assert.True(number.IsFinite);
        Assert.False(number.IsNaN);
        Assert.False(number.IsInfinity);
        Assert.Equal(BigInteger.Parse(coefficient, CultureInfo.InvariantCulture), number.Coefficient);
        Assert.Equal(scale, number.Scale);
        Assert.Equal(formatted, number.ToString());
        Assert.Equal(number, KingbaseNumeric.Parse(number.ToString()));
        Assert.True(KingbaseNumeric.TryParse(text, out var parsed));
        Assert.Equal(number, parsed);
    }

    [Fact]
    public void DefaultIsFiniteZeroAndConstructorPreservesScale()
    {
        var zero = default(KingbaseNumeric);
        Assert.Equal(BigInteger.Zero, zero.Coefficient);
        Assert.Equal(0, zero.Scale);
        Assert.True(zero.IsFinite);
        Assert.Equal("0", zero.ToString());
        Assert.Equal(0M, zero.ToDecimal());
        var number = new KingbaseNumeric(-123400, 3);
        Assert.Equal(new BigInteger(-123400), number.Coefficient);
        Assert.Equal(3, number.Scale);
        Assert.Equal("-123.400", number.ToString());
        Assert.Equal(zero, new KingbaseNumeric(0, 100));
    }

    [Theory]
    [InlineData("NaN", false, true)]
    [InlineData("Infinity", true, false)]
    [InlineData("-Infinity", true, false)]
    public void SpecialsHaveExplicitStateAndNoFiniteParts(string text, bool infinity, bool nan)
    {
        var value = KingbaseNumeric.Parse(text);
        Assert.False(value.IsFinite);
        Assert.Equal(infinity, value.IsInfinity);
        Assert.Equal(nan, value.IsNaN);
        Assert.Equal(text, value.ToString());
        Assert.Throws<InvalidOperationException>(() => value.Coefficient);
        Assert.Throws<InvalidOperationException>(() => value.Scale);
        Assert.Throws<OverflowException>(() => value.ToDecimal());
        Assert.Equal(value, KingbaseNumeric.Parse(text));
        Assert.NotEqual(default, value);
        Assert.True(KingbaseNumeric.NaN.IsNaN);
        Assert.Equal("Infinity", KingbaseNumeric.PositiveInfinity.ToString());
        Assert.Equal("-Infinity", KingbaseNumeric.NegativeInfinity.ToString());
        Assert.NotEqual(KingbaseNumeric.PositiveInfinity, KingbaseNumeric.NegativeInfinity);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,000")]
    [InlineData("1_000")]
    [InlineData("1.2.3")]
    [InlineData("+")]
    [InlineData(".")]
    [InlineData("+.")]
    [InlineData("1e")]
    [InlineData("1e-")]
    [InlineData("1e1.2")]
    [InlineData("１２")]
    [InlineData("nan")]
    [InlineData("+Infinity")]
    [InlineData("secret-numeric")]
    public void InvalidSyntaxHasSafeErrorsAndTryParseReturnsFalse(string text)
    {
        var error = Assert.Throws<FormatException>(() => KingbaseNumeric.Parse(text));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.False(KingbaseNumeric.TryParse(text, out var value));
        Assert.Equal(default, value);
    }

    [Fact]
    public void NullInvalidUnicodeAndLargeSyntaxAreBounded()
    {
        Assert.Throws<ArgumentNullException>(() => KingbaseNumeric.Parse(null!));
        Assert.False(KingbaseNumeric.TryParse(null, out _));
        foreach (var text in new[] { "1\0", new string('\ud800', 1) })
        {
            Assert.Throws<FormatException>(() => KingbaseNumeric.Parse(text));
            Assert.False(KingbaseNumeric.TryParse(text, out _));
        }
        Assert.Throws<OverflowException>(() => KingbaseNumeric.Parse(new string('0', 40000)));
        Assert.False(KingbaseNumeric.TryParse(new string('0', 40000), out _));
    }

    [Fact]
    public void LimitsBoundConstructorParsingAndExponentExpansion()
    {
        Assert.Equal(16384, KingbaseNumeric.MaxDigits);
        Assert.Equal(16384, KingbaseNumeric.MaxScale);
        var largest = BigInteger.Pow(10, 16384) - 1;
        var value = new KingbaseNumeric(largest, 0);
        Assert.Equal(largest, KingbaseNumeric.Parse(value.ToString()).Coefficient);
        Assert.Equal(largest, KingbaseNumeric.Parse("000" + value).Coefficient);
        var smallest = new KingbaseNumeric(-1, 16384);
        Assert.Equal(smallest, KingbaseNumeric.Parse(smallest.ToString()));
        Assert.Equal(16384, smallest.Scale);
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseNumeric(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseNumeric(1, 16385));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KingbaseNumeric(largest + 1, 0));
        foreach (var text in new[] { "1e16384", "1e-16385", "1e2147483647", "1e-2147483648", "1e99999999999999999999999999", new string('9', 16385) })
        {
            Assert.Throws<OverflowException>(() => KingbaseNumeric.Parse(text));
            Assert.False(KingbaseNumeric.TryParse(text, out _));
        }
        Assert.Equal(new KingbaseNumeric(1, 16384), KingbaseNumeric.Parse("1e-16384"));
        Assert.Equal(BigInteger.Pow(10, 16383), KingbaseNumeric.Parse("1e16383").Coefficient);
    }

    public static TheoryData<decimal> DecimalValues => new()
    {
        0M, 1M, -1M, 1.2300M, -0.0000000000000000000000000001M,
        decimal.MaxValue, decimal.MinValue, 7922816251426433759354395033.5M
    };

    [Theory]
    [MemberData(nameof(DecimalValues))]
    public void DecimalRoundTripsExactlyAndPreservesCoefficientAndScale(decimal value)
    {
        var numeric = KingbaseNumeric.FromDecimal(value);
        Assert.Equal(decimal.GetBits(value), decimal.GetBits(numeric.ToDecimal()));
        Assert.Equal(value, KingbaseNumeric.Parse(numeric.ToString()).ToDecimal());
        Assert.Equal((decimal.GetBits(value)[3] >> 16) & 255, numeric.Scale);
    }

    [Theory]
    [InlineData("79228162514264337593543950335.0", "79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001000", "0.0000000000000000000000000001")]
    [InlineData("0.0000000000000000000000000000000", "0")]
    public void DecimalCanRemoveOnlyTrailingZeroDigits(string text, string exact)
    {
        Assert.Equal(decimal.Parse(exact, CultureInfo.InvariantCulture), KingbaseNumeric.Parse(text).ToDecimal());
    }

    [Theory]
    [InlineData("79228162514264337593543950336")]
    [InlineData("-79228162514264337593543950336")]
    [InlineData("79228162514264337593543950335.1")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("-0.000000000000000000000000000001")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void DecimalNeverRoundsUnrepresentableValues(string text)
    {
        var value = KingbaseNumeric.Parse(text);
        Assert.Throws<OverflowException>(() => value.ToDecimal());
        Assert.Equal(value, KingbaseNumeric.Parse(value.ToString()));
    }

    [Theory]
    [InlineData("1", "1.000")]
    [InlineData("-123.45", "-123.45000")]
    [InlineData("0.00", "0e100")]
    [InlineData("NaN", "NaN")]
    public void EqualityAndHashAreNumericalAcrossScales(string a, string b)
    {
        var first = KingbaseNumeric.Parse(a);
        var second = KingbaseNumeric.Parse(b);
        Assert.True(first.Equals(second));
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first.Equals((object)second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(1M));
        Assert.NotEqual(KingbaseNumeric.Parse("1.2"), KingbaseNumeric.Parse("12"));
        Assert.NotEqual(KingbaseNumeric.Parse("1.2"), KingbaseNumeric.Parse("-1.2"));
    }

    [Fact]
    public void HugePrecisionAndInvariantFormattingDoNotDependOnCurrentCulture()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var text = "-" + new string('9', 1000) + ".00100";
            var value = KingbaseNumeric.Parse(text);
            Assert.Equal(text, value.ToString());
            Assert.Equal(5, value.Scale);
            Assert.Throws<OverflowException>(() => value.ToDecimal());
            Assert.False(KingbaseNumeric.TryParse("1,23", out _));
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }
}
