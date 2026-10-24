using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class ReadAloudTextTests
{
    private const string Page =
        "Chapter One\n" +
        "The quick brown fox jumps over the lazy dog and keeps on run-\n" +
        "ning across the field until the evening comes and the light fades.\n" +
        "It stopped.\n" +
        "A second paragraph begins here and goes on for quite a while too,\n" +
        "ending on this line.\n" +
        "\n" +
        "• First point of a list\n" +
        "• Second point";

    [Fact]
    public void SplitsParagraphsAndJoinsLines()
    {
        var chunks = ReadAloudText.Chunks(3, Page);
        Assert.Equal(
        [
            "Chapter One",
            "The quick brown fox jumps over the lazy dog and keeps on running across the field until the evening comes and the light fades. It stopped.",
            "A second paragraph begins here and goes on for quite a while too, ending on this line.",
            "• First point of a list",
            "• Second point",
        ], chunks.Select(c => c.Text));
        Assert.All(chunks, c => Assert.Equal(3, c.Page));
    }

    [Fact]
    public void MapsSpokenTextBackToThePage()
    {
        var chunks = ReadAloudText.Chunks(0, Page);
        foreach (var c in chunks)
        {
            Assert.Equal(c.Text.Length, c.Source.Length);
            for (var i = 0; i < c.Text.Length; i++)
                if (c.Text[i] != ' ') Assert.Equal(c.Text[i], Page[c.Source[i]]);
        }
        // "running" spans the hyphenated line break: its page range covers "run-\nning".
        var p = chunks[1];
        var at = p.Text.IndexOf("running", StringComparison.Ordinal);
        var (s, e) = p.SourceRange(at, at + "running".Length);
        Assert.Equal("run-\nning", Page[s..e]);
    }

    [Fact]
    public void ReadsFromAGivenPosition()
    {
        var from = Page.IndexOf("A second", StringComparison.Ordinal);
        var to = Page.IndexOf("too,", StringComparison.Ordinal) + 3;
        var chunks = ReadAloudText.Chunks(0, Page, from, to);
        Assert.Equal("A second paragraph begins here and goes on for quite a while too", Assert.Single(chunks).Text);
        Assert.Equal(from, chunks[0].SourceStart);
    }

    [Fact]
    public void CutsLongParagraphsAtSentenceEnds()
    {
        var text = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"Sentence number {i} is here."));
        var chunks = ReadAloudText.Chunks(0, text, maxLength: 200);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 200));
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text));
        Assert.Equal(text, string.Join(" ", chunks.Select(c => c.Text)));
    }

    [Fact]
    public void SkipsEmptyAndPunctuationOnlyText()
    {
        Assert.Empty(ReadAloudText.Chunks(0, ""));
        Assert.Empty(ReadAloudText.Chunks(0, "\n\n . \n"));
    }
}
