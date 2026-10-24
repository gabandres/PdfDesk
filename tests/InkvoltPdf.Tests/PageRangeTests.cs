using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class PageRangeTests
{
    [Theory]
    [InlineData("1-3, 5", 10, new[] { 0, 1, 2, 4 })]
    [InlineData("8-", 10, new[] { 7, 8, 9 })]
    [InlineData("-2", 10, new[] { 0, 1 })]
    [InlineData("3;1;3", 10, new[] { 0, 2 })]
    [InlineData("2-end", 4, new[] { 1, 2, 3 })]
    public void Parses(string text, int count, int[] expected)
    {
        Assert.True(PageRange.TryParse(text, count, out var pages, out _));
        Assert.Equal(expected, pages);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("5-3")]
    [InlineData("abc")]
    [InlineData("")]
    public void Rejects(string text)
    {
        Assert.False(PageRange.TryParse(text, 10, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void FormatsCompactly() => Assert.Equal("1-3, 5, 9-10", PageRange.Format([0, 1, 2, 4, 8, 9]));

    [Fact]
    public void EvenOddUseOneBasedNumbers()
    {
        Assert.Equal([1, 3], PageRange.Apply([0, 1, 2, 3], PageSubset.Even));
        Assert.Equal([0, 2], PageRange.Apply([0, 1, 2, 3], PageSubset.Odd));
    }
}
