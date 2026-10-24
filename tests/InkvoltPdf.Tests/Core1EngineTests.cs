using System.Diagnostics;
using MuPDFCore;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class Core1EngineTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void CancelledRenderThrows()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(1), InputFileTypes.PDF, null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Render(0, 1, null, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => engine.SaveImage(0, 72, Path.Combine(Path.GetTempPath(), "never.png"), false, cts.Token));
    }

    [Theory]
    [InlineData(0.5, 0, 0, 612, 792)]
    [InlineData(1.37, 13.3, 7.9, 201.7, 99.1)]
    [InlineData(2.91, 300.25, 500.5, 311.75, 291.5)]
    [InlineData(0.13, 1, 1, 600, 700)]
    public void CancellableRenderMatchesPlainRender(double scale, double x, double y, double w, double h)
    {
        using var engine = PdfEngine.Open(TestPdf.Make(1), InputFileTypes.PDF, null);
        using var cts = new CancellationTokenSource();
        var region = new RectD(x, y, w, h);
        var plain = engine.Render(0, scale, region);
        var cancellable = engine.Render(0, scale, region, cts.Token);
        Assert.Equal(plain.Width, cancellable.Width);
        Assert.Equal(plain.Height, cancellable.Height);
        Assert.Equal(plain.Pixels, cancellable.Pixels);
    }

    [Fact]
    public void RenderStopsMidwayWhenCancelled()
    {
        using var engine = PdfEngine.Open(Core1TestPdf.Heavy(), InputFileTypes.PDF, null);
        var full = Stopwatch.StartNew();
        engine.Render(0, 1);
        full.Stop();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var cut = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Render(0, 1, null, cts.Token));
        cut.Stop();
        output.WriteLine($"full {full.ElapsedMilliseconds} ms, cancelled after {cut.ElapsedMilliseconds} ms");
        if (full.ElapsedMilliseconds > 400)
            Assert.True(cut.ElapsedMilliseconds < full.ElapsedMilliseconds * 0.6, $"cancelled after {cut.ElapsedMilliseconds} ms; full render {full.ElapsedMilliseconds} ms");

        // The engine is fine afterwards.
        Assert.Equal(612, engine.Render(0, 0.1, null, CancellationToken.None).Width * 10, 10.0);
    }

    [Fact]
    public async Task TextDoesNotWaitForARender()
    {
        var data = Core1TestPdf.Heavy();
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var full = Stopwatch.StartNew();
        engine.Render(0, 1);
        full.Stop();
        if (full.ElapsedMilliseconds < 300) return; // too fast here to tell

        using var started = new ManualResetEventSlim();
        var render = Task.Run(() =>
        {
            started.Set();
            return engine.Render(0, 1);
        });
        started.Wait();
        Thread.Sleep(20);
        var text = Stopwatch.StartNew();
        engine.GetText(0);
        engine.GetLinks(0);
        engine.GetOutline();
        text.Stop();
        output.WriteLine($"text {text.ElapsedMilliseconds} ms during a {full.ElapsedMilliseconds} ms render (done: {render.IsCompleted})");
        Assert.False(render.IsCompleted && text.ElapsedMilliseconds > full.ElapsedMilliseconds / 2,
            $"text took {text.ElapsedMilliseconds} ms during a {full.ElapsedMilliseconds} ms render");
        await render;
    }

    [Fact]
    public void RefusesHugeImages()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(1), InputFileTypes.PDF, null);
        Assert.Contains("too large", Assert.Throws<InvalidOperationException>(() => engine.Render(0, 100)).Message);
        Assert.Contains("too large", Assert.Throws<InvalidOperationException>(() => engine.SaveImage(0, 8000, Path.Combine(Path.GetTempPath(), "never.png"), false)).Message);
        Assert.Equal(1224, engine.Render(0, 2).Width, 1.0);
    }

    [Fact]
    public void EncryptedDocumentTextUsesThePassword()
    {
        using var engine = PdfEngine.Open(TestPdf.Encrypted(2, "pw"), InputFileTypes.PDF, "pw");
        Assert.Contains("Page 2", engine.GetText(1).Text);
        Assert.Empty(engine.GetOutline());
    }

    [Fact]
    public void PreviewEngineReadsTextFromTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inkvoltpdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, TestPdf.Make(2));
        try
        {
            using var engine = PdfEngine.OpenPreview(path)!;
            Assert.Contains("Page 2", engine.GetText(1).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeWhileTextIsInUseIsSafe()
    {
        for (var i = 0; i < 5; i++)
        {
            var engine = PdfEngine.Open(TestPdf.Make(3), InputFileTypes.PDF, null);
            var t = Task.Run(() =>
            {
                try
                {
                    for (var p = 0; p < 3; p++) engine.GetText(p, cache: false);
                }
                catch (ObjectDisposedException)
                {
                }
            });
            engine.Render(0, 0.2);
            engine.Dispose();
            await t;
            Assert.Throws<ObjectDisposedException>(() => engine.GetText(0));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

public class Core1TextTests
{
    private static PageText Lines(params string[] lines) =>
        new([.. lines.Select((line, y) => line.Select((c, x) => (c.ToString(), new RectD(x * 10, y * 20, 10, 16))).ToList())]);

    [Fact]
    public void FindsWordsHyphenatedAcrossLines()
    {
        var text = Lines("an exam-", "ple here");
        var hits = text.Find("example", false, wholeWord: true);
        Assert.Single(hits);
        Assert.Equal("exam-\nple", text.GetString(hits[0].Start, hits[0].End));
        Assert.Equal(2, text.GetRects(hits[0].Start, hits[0].End).Count);

        var soft = Lines("an exam­", "ple here");
        Assert.Equal("exam­\nple", soft.GetString(soft.Find("example", false, false)[0].Start, soft.Find("example", false, false)[0].End));

        // Numbers and dashes at line ends stay as they are; queries spanning other line breaks still work.
        Assert.Empty(Lines("1990-", "2000").Find("19902000", false, false));
        Assert.Single(Lines("1990-", "2000").Find("1990- 2000", false, false));
        Assert.Equal((3, 17), Lines("an exam-", "ple here").Find("example here", false, false)[0]);
    }

    [Fact]
    public void IgnoresAccentsOnRequest()
    {
        var text = Lines("Un café crème");
        Assert.Empty(text.Find("cafe", false, false));
        var hit = Assert.Single(text.Find("cafe creme", false, false, ignoreAccents: true));
        Assert.Equal("café crème", text.GetString(hit.Start, hit.End));
        Assert.Single(text.Find("CAFÉ", false, true));
        Assert.Empty(text.Find("CAFÉ", true, true));
    }

    [Fact]
    public void MatchLengthComesFromTheComparer()
    {
        // A decomposed "é" (e + combining acute) is one letter to the comparer but two UTF-16 units in the text.
        var text = Lines("café au lait");
        var hit = Assert.Single(text.Find("café au", false, false));
        Assert.Equal("café au", text.GetString(hit.Start, hit.End));
        Assert.Single(text.Find("café", false, wholeWord: true));
    }

    [Fact]
    public void IgnorableOnlyQueryFindsNothing() => Assert.Empty(Lines("abc").Find("­", false, false));

    [Theory]
    [InlineData("2–4")]
    [InlineData("2 — 4")]
    [InlineData("2-4")]
    public void PageRangesAcceptTypographicDashes(string text)
    {
        Assert.True(PageRange.TryParse(text, 5, out var pages, out _));
        Assert.Equal([1, 2, 3], pages);
        Assert.False(PageRange.TryParse("+2", 5, out _, out _));
    }
}

public class Core1SessionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"inkvoltpdf-{Guid.NewGuid():N}.pdf");

    public Core1SessionTests() => File.WriteAllBytes(_path, TestPdf.Make(3));

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.SetAttributes(_path, FileAttributes.Normal);
            File.Delete(_path);
        }
        foreach (var temp in Directory.GetFiles(Path.GetTempPath(), $".{Path.GetFileName(_path)}.*.tmp")) File.Delete(temp);
    }

    [Fact]
    public void SaveReplacesTheFileKeepingItsCreationTime()
    {
        var created = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetCreationTimeUtc(_path, created);
        using var session = DocumentSession.Open(_path);
        session.Commit(PageEditor.Delete(session.Data, null, [0]), "Delete");
        session.SaveAs(_path);
        Assert.Equal(created, File.GetCreationTimeUtc(_path));
        Assert.Equal([2, 3], TestPdf.PageNumbers(File.ReadAllBytes(_path)));
        Assert.False(session.IsDirty);
        Assert.False(session.HasChangedOnDisk());
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $".{Path.GetFileName(_path)}.*.tmp"));

        var other = Path.Combine(Path.GetTempPath(), $"inkvoltpdf-{Guid.NewGuid():N}.pdf");
        try
        {
            session.SaveAs(other);
            Assert.Equal(other, session.FilePath);
            Assert.Equal([2, 3], TestPdf.PageNumbers(File.ReadAllBytes(other)));
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public void SavingOverAReadOnlyFileSaysSo()
    {
        using var session = DocumentSession.Open(_path);
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        var ex = Assert.Throws<UnauthorizedAccessException>(() => session.SaveAs(_path));
        Assert.Contains("is read-only", ex.Message);
        Assert.Equal(3, TestPdf.PageNumbers(File.ReadAllBytes(_path)).Length);
    }

    [Fact]
    public void DetectsAndReloadsExternalChanges()
    {
        using var session = DocumentSession.Open(_path);
        Assert.False(session.HasChangedOnDisk());
        Assert.False(session.IsMissingOnDisk());
        session.Commit(PageEditor.Rotate(session.Data, null, [0], 90), "Rotate");

        File.WriteAllBytes(_path, TestPdf.Make(5, "beta"));
        Assert.True(session.HasChangedOnDisk());

        var events = new List<string>();
        session.ContentChanged += (_, _) => events.Add("content:" + session.Engine.PageCount);
        session.StateChanged += (_, _) => events.Add("state:" + session.IsDirty);
        session.ReloadFromDisk();
        Assert.Equal(["content:5", "state:False"], events);
        Assert.Equal(5, session.Engine.PageCount);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.False(session.IsDirty);
        Assert.False(session.HasChangedOnDisk());
        Assert.Contains("beta", session.Engine.GetText(0).Text);

        // Editing and undoing back to the reloaded state is clean again.
        session.Commit(PageEditor.Delete(session.Data, null, [0]), "Delete");
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void DetectsAMissingFile()
    {
        using var session = DocumentSession.Open(_path);
        File.Delete(_path);
        Assert.True(session.IsMissingOnDisk());
        Assert.True(session.HasChangedOnDisk());
        Assert.Throws<FileNotFoundException>(() => session.ReloadFromDisk());
        Assert.Equal(3, session.Engine.PageCount); // a failed reload changes nothing
    }

    [Fact]
    public void UnsavedDocumentsHaveNoDiskState()
    {
        using var session = DocumentSession.FromBytes(TestPdf.Make(1), "New.pdf");
        Assert.False(session.HasChangedOnDisk());
        Assert.False(session.IsMissingOnDisk());
        Assert.Throws<InvalidOperationException>(() => session.ReloadFromDisk());
    }
}
