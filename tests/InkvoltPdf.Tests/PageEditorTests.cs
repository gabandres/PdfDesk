using MuPDFCore;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class PageEditorTests
{
    private static readonly byte[] Five = TestPdf.Make(5);

    [Fact]
    public void DeletesPages() => Assert.Equal([1, 3, 5], TestPdf.PageNumbers(PageEditor.Delete(Five, null, [1, 3])));

    [Fact]
    public void RefusesToDeleteEveryPage() =>
        Assert.Throws<InvalidOperationException>(() => PageEditor.Delete(Five, null, [0, 1, 2, 3, 4]));

    [Fact]
    public void ReordersPages() => Assert.Equal([5, 1, 4, 2, 3], TestPdf.PageNumbers(PageEditor.Reorder(Five, null, [4, 0, 3, 1, 2])));

    [Theory]
    [InlineData(new[] { 3, 4 }, 0, new[] { 4, 5, 1, 2, 3 })]
    [InlineData(new[] { 0 }, 5, new[] { 2, 3, 4, 5, 1 })]
    [InlineData(new[] { 0, 2 }, 4, new[] { 2, 4, 1, 3, 5 })]
    [InlineData(new[] { 1 }, 1, new[] { 1, 2, 3, 4, 5 })]
    public void MovesPages(int[] pages, int before, int[] expected) =>
        Assert.Equal(expected, TestPdf.PageNumbers(PageEditor.Move(Five, null, 5, pages, before)));

    [Fact]
    public void ReversesPages() => Assert.Equal([5, 4, 3, 2, 1], TestPdf.PageNumbers(PageEditor.Reverse(Five, null, 5)));

    [Fact]
    public void RotatesPages()
    {
        var rotated = PageEditor.Rotate(Five, null, [0], 90);
        using var engine = PdfEngine.Open(rotated, InputFileTypes.PDF, null);
        Assert.Equal(792, engine.GetPage(0).Width, 0.5);
        Assert.Equal(612, engine.GetPage(0).Height, 0.5);
        Assert.Equal(612, engine.GetPage(1).Width, 0.5);

        var back = PageEditor.Rotate(rotated, null, [0], -90);
        using var engine2 = PdfEngine.Open(back, InputFileTypes.PDF, null);
        Assert.Equal(612, engine2.GetPage(0).Width, 0.5);
    }

    [Fact]
    public void InsertsPagesFromAnotherFile()
    {
        var other = TestPdf.Make(3, "beta");
        var result = PageEditor.Insert(Five, null, new PdfSource(other, null, "other.pdf"), [2, 0], 1);
        Assert.Equal([1, 3, 1, 2, 3, 4, 5], TestPdf.PageNumbers(result));
        using var engine = PdfEngine.Open(result, InputFileTypes.PDF, null);
        Assert.Contains("beta", engine.GetText(1).Text);
        Assert.Contains("alpha", engine.GetText(3).Text);
    }

    [Fact]
    public void InsertsBlankPages()
    {
        var result = PageEditor.InsertBlank(Five, null, 5, 2, 595, 842);
        Assert.Equal([1, 2, 3, 4, 5, 0, 0], TestPdf.PageNumbers(result));
        using var engine = PdfEngine.Open(result, InputFileTypes.PDF, null);
        Assert.Equal(595, engine.GetPage(6).Width, 0.5);
        Assert.Equal(842, engine.GetPage(6).Height, 0.5);
    }

    [Fact]
    public void DuplicatesPages() => Assert.Equal([1, 1, 2, 3, 3, 4, 5], TestPdf.PageNumbers(PageEditor.Duplicate(Five, null, [0, 2])));

    [Fact]
    public void ExtractsPages() => Assert.Equal([2, 4], TestPdf.PageNumbers(PageEditor.Extract(Five, null, [3, 1])));

    [Fact]
    public void MergesWithBookmarks()
    {
        var merged = PageEditor.Merge([new PdfSource(TestPdf.Make(2), null, "a.pdf"), new PdfSource(TestPdf.Make(3, "beta"), null, "b.pdf")], true);
        Assert.Equal([1, 2, 1, 2, 3], TestPdf.PageNumbers(merged));
        using var engine = PdfEngine.Open(merged, InputFileTypes.PDF, null);
        var outline = engine.GetOutline();
        Assert.Equal(["a", "b"], outline.Select(o => o.Title));
        Assert.Equal([0, 2], outline.Select(o => o.PageIndex));
    }

    [Fact]
    public void CropsAndUncrops()
    {
        var cropped = PageEditor.Crop(Five, null, [0], new Margins(72, 36, 0, 0));
        using (var engine = PdfEngine.Open(cropped, InputFileTypes.PDF, null))
        {
            Assert.Equal(540, engine.GetPage(0).Width, 0.5);
            Assert.Equal(756, engine.GetPage(0).Height, 0.5);
            // "Page 1" was drawn at x = 72pt; with 72pt cropped off the left it now starts near x = 0.
            var text = engine.GetText(0);
            var hit = text.Find("Page 1", true, false)[0];
            Assert.InRange(text.GetRects(hit.Start, hit.End)[0].X, -2, 8);
        }

        var restored = PageEditor.Uncrop(cropped, null, [0]);
        using var engine2 = PdfEngine.Open(restored, InputFileTypes.PDF, null);
        Assert.Equal(612, engine2.GetPage(0).Width, 0.5);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CropMarginsFollowRotation(int rotation)
    {
        // Crop 100pt off the visible top of a rotated page: visible height shrinks by 100 and the text moves up 100.
        var rotated = PageEditor.Rotate(Five, null, [0], rotation);
        using var before = PdfEngine.Open(rotated, InputFileTypes.PDF, null);
        var cropped = PageEditor.Crop(rotated, null, [0], new Margins(0, 100, 0, 0));
        using var after = PdfEngine.Open(cropped, InputFileTypes.PDF, null);
        Assert.Equal(before.GetPage(0).Width, after.GetPage(0).Width, 0.5);
        Assert.Equal(before.GetPage(0).Height - 100, after.GetPage(0).Height, 0.5);

        var a = Locate(before);
        var b = Locate(after);
        Assert.Equal(a.X, b.X, 1.0);
        Assert.Equal(a.Y - 100, b.Y, 1.0);

        static RectD Locate(PdfEngine e)
        {
            var t = e.GetText(0);
            var h = t.Find("Page 1", true, false)[0];
            return t.GetRects(h.Start, h.End)[0];
        }
    }

    [Fact]
    public void ReadsAndWritesMetadata()
    {
        var updated = PageEditor.WriteMetadata(Five, null, "New title", "Me", "Subj", "k1, k2");
        var meta = PageEditor.ReadMetadata(updated, null);
        Assert.Equal("New title", meta.Title);
        Assert.Equal("Me", meta.Author);
        Assert.Equal("k1, k2", meta.Keywords);
    }

    [Fact]
    public void EditsEncryptedDocumentWithOwnerPassword()
    {
        var locked = TestPdf.Encrypted(3, "secret");
        Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(locked, InputFileTypes.PDF, null));
        Assert.True(Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(locked, InputFileTypes.PDF, "nope")).WrongPassword);

        // Editing needs the owner password; the open (user) password alone is refused with a clear message.
        Assert.Contains("owner", Assert.Throws<OwnerPasswordRequiredException>(() => PageEditor.Delete(locked, "secret", [0])).Message);
        var edited = PageEditor.Delete(locked, "secret-owner", [0]);
        // The edit keeps the protection: the same open password (recovered from the owner password) still opens it.
        Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(edited, InputFileTypes.PDF, null));
        using var engine = PdfEngine.Open(edited, InputFileTypes.PDF, "secret");
        Assert.True(engine.IsEncrypted);
        Assert.Equal(2, engine.PageCount);
        Assert.Contains("Page 2", engine.GetText(0).Text);
    }

    [Fact]
    public void UndoingEveryEditMakesTheSessionCleanAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inkvoltpdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Five);
        try
        {
            using var session = DocumentSession.Open(path);
            session.Commit(PageEditor.Rotate(session.Data, null, [1, 2], 90), "Rotate");
            session.Commit(PageEditor.Delete(session.Data, null, [3]), "Delete");
            session.Undo();
            Assert.True(session.IsDirty);
            session.Undo();
            Assert.False(session.IsDirty);
            session.Redo();
            Assert.True(session.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SessionUndoRedoAndSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inkvoltpdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Five);
        try
        {
            using var session = DocumentSession.Open(path);
            session.Commit(PageEditor.Delete(session.Data, null, [0]), "Delete");
            Assert.Equal(4, session.Engine.PageCount);
            Assert.True(session.IsDirty);
            session.Undo();
            Assert.Equal(5, session.Engine.PageCount);
            Assert.False(session.IsDirty); // back at the saved state
            session.Redo();
            Assert.True(session.IsDirty);
            Assert.Equal(4, session.Engine.PageCount);
            session.SaveAs(path);
            Assert.False(session.IsDirty);
            Assert.Equal([2, 3, 4, 5], TestPdf.PageNumbers(File.ReadAllBytes(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
