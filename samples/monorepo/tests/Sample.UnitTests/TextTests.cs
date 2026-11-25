using Sample.Core;
using Xunit;

namespace Sample.UnitTests;

public sealed class TokenizerTests
{
    [Fact]
    public void SplitsOnPunctuationAndLowercases() => Assert.Equal(["hello", "world", "42"], Tokenizer.Words("Hello, World! 42"));

    [Fact]
    public void KeepsInnerApostrophes() => Assert.Equal(["don't", "stop"], Tokenizer.Words("Don't stop'"));

    [Fact]
    public void EmptyTextHasNoWords() => Assert.Empty(Tokenizer.Words("  ,;  "));

    [Fact]
    public void CountsRepeats()
    {
        var counts = Tokenizer.Counts("a b A c b a");
        Assert.Equal((3, 2, 1), (counts["a"], counts["b"], counts["c"]));
    }

    [Fact]
    public void LettersOutsideAsciiCount() => Assert.Equal(["grüße", "café"], Tokenizer.Words("Grüße café"));
}

public sealed class CsvLineTests
{
    [Fact]
    public void PlainFields() => Assert.Equal(["a", "b", "c"], CsvLine.Parse("a,b,c"));

    [Fact]
    public void EmptyFields() => Assert.Equal(["", "", ""], CsvLine.Parse(",,"));

    [Fact]
    public void QuotedCommaAndQuote() => Assert.Equal(["x,y", "say \"hi\""], CsvLine.Parse("\"x,y\",\"say \"\"hi\"\"\""));

    [Fact]
    public void UnterminatedQuoteFails() => Assert.Throws<FormatException>(() => CsvLine.Parse("\"open,b"));

    [Theory]
    [InlineData("plain")]
    [InlineData("with,comma")]
    [InlineData("with \"quote\"")]
    [InlineData("")]
    public void FormatRoundTrips(string field) => Assert.Equal([field, "z"], CsvLine.Parse(CsvLine.Format([field, "z"])));
}

public sealed class SlugTests
{
    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("  --Many   spaces--  ", "many-spaces")]
    [InlineData("C# & .NET 10!", "c-net-10")]
    [InlineData("", "")]
    public void Slugifies(string title, string slug) => Assert.Equal(slug, Slug.From(title));

    [Fact]
    public void CutsAtMaxLengthWithoutTrailingHyphen() => Assert.Equal("abc", Slug.From("abc def", 4));
}
