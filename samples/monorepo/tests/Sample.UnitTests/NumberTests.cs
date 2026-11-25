using Sample.Core;
using Xunit;

namespace Sample.UnitTests;

public sealed class RomanNumeralTests
{
    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(1994, "MCMXCIV")]
    [InlineData(3999, "MMMCMXCIX")]
    public void ToAndFrom(int n, string roman)
    {
        Assert.Equal(roman, RomanNumerals.To(n));
        Assert.Equal(n, RomanNumerals.From(roman));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4000)]
    public void OutOfRange(int n) => Assert.Throws<ArgumentOutOfRangeException>(() => RomanNumerals.To(n));

    [Theory]
    [InlineData("IIII")]
    [InlineData("ABC")]
    [InlineData("")]
    [InlineData("IC")]
    public void RefusesNonCanonical(string roman) => Assert.Throws<FormatException>(() => RomanNumerals.From(roman));
}

public sealed class RomanNumeralSweepTests
{
    /// <summary>Every number round-trips (a medium-length class: a few thousand conversions).</summary>
    [Fact]
    public void AllNumbersRoundTrip()
    {
        for (var n = 1; n <= 3999; n++)
        {
            Assert.Equal(n, RomanNumerals.From(RomanNumerals.To(n)));
        }
    }
}

public sealed class LuhnTests
{
    [Theory]
    [InlineData("79927398713", true)]
    [InlineData("79927398710", false)]
    [InlineData("4111111111111111", true)]
    [InlineData("0", false)]
    [InlineData("12a4", false)]
    public void Validates(string digits, bool valid) => Assert.Equal(valid, Luhn.IsValid(digits));

    [Fact]
    public void CheckDigitCompletesANumber()
    {
        Assert.Equal(3, Luhn.CheckDigit("7992739871"));
        Assert.True(Luhn.IsValid("7992739871" + Luhn.CheckDigit("7992739871")));
    }

    [Fact]
    public void CheckDigitRefusesLetters() => Assert.Throws<FormatException>(() => Luhn.CheckDigit("12x"));
}

public sealed class PaginatorTests
{
    private static readonly int[] Items = [.. Enumerable.Range(1, 23)];

    [Fact]
    public void FirstPage()
    {
        var page = Paginator.Get(Items, 1, 10);
        Assert.Equal((1, 3, 23, 10), (page.Number, page.PageCount, page.TotalItems, page.Items.Count));
    }

    [Fact]
    public void LastPageIsShort() => Assert.Equal([21, 22, 23], Paginator.Get(Items, 3, 10).Items);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 3)]
    public void ClampsThePageNumber(int asked, int got) => Assert.Equal(got, Paginator.Get(Items, asked, 10).Number);

    [Fact]
    public void EmptyListHasOnePage() => Assert.Equal((1, 0), (Paginator.Get(Array.Empty<int>(), 1, 5).PageCount, Paginator.Get(Array.Empty<int>(), 1, 5).Items.Count));

    [Fact]
    public void SizeMustBePositive() => Assert.Throws<ArgumentOutOfRangeException>(() => Paginator.Get(Items, 1, 0));
}
