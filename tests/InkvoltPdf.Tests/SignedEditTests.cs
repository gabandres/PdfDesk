using System.Diagnostics;
using System.Text;
using InkvoltPdf.Core;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace InkvoltPdf.Tests;

/// <summary>Edits of signed documents: when they can't be appended, removed items left in the signed revision, memory.</summary>
public class SignedEditTests(ITestOutputHelper output)
{
    private const string Placeholder = "[0 0000000000 0000000000 0000000000]";

    /// <summary>
    /// A signed two-page form (page 2 says "Second page"), plus <paramref name="pages"/> pages holding about
    /// <paramref name="megabytes"/> MB of incompressible image data; <paramref name="trailerExtra"/> goes into the trailer
    /// and <paramref name="junk"/> after %%EOF (both before signing, so the signature covers them).
    /// </summary>
    internal static byte[] Signed(int pages = 0, int megabytes = 0, string trailerExtra = "", string junk = "")
    {
        var kids = string.Join(' ', Enumerable.Range(0, pages).Select(i => $" {11 + 3 * i} 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 6 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> /SigFlags 3 >> >>",
            $"<< /Type /Pages /Kids [3 0 R 9 0 R{kids}] /Count {pages + 2} >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 6 0 R] /Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approval) /Rect [72 650 272 700] /V 5 0 R /P 3 0 R /F 4 >>",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange {Placeholder} /Contents <{new string('0', IncrSignatureTests.ContentsHexLength)}> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [72 500 300 530] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Hello contract) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 10 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Second page) Tj ET"),
        };
        var rng = new Random(7);
        var side = pages == 0 ? 0 : (int)Math.Sqrt(megabytes * 1024.0 * 1024 / pages / 3);
        for (var i = 0; i < pages; i++)
        {
            var n = 11 + 3 * i;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {n + 1} 0 R /Resources << /XObject << /Im0 {n + 2} 0 R >> >> >>");
            objects.Add(Core2Pdf.Stream("q 500 0 0 500 50 150 cm /Im0 Do Q"));
            var pixels = new char[side * side * 3];
            for (var k = 0; k < pixels.Length; k++) pixels[k] = (char)rng.Next(256);
            objects.Add(Core2Pdf.Stream(new string(pixels), $"/Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceRGB /BitsPerComponent 8"));
        }
        var pdf = Core2Pdf.Build(objects, trailerExtra);
        if (junk.Length > 0) pdf = [.. pdf, .. Encoding.Latin1.GetBytes(junk)];
        return IncrSignatureTests.Sign(pdf, Placeholder);
    }

    private static SignatureStatus Approval(byte[] pdf) => Signatures.Verify(pdf).Single(s => s.FieldName == "Approval").Status;

    private static bool Appended(byte[] original, byte[] edited) =>
        edited.Length > original.Length && edited.AsSpan(0, original.Length).SequenceEqual(original);

    /// <summary>The same document saved encrypted: still signed-looking (its /ByteRange is in the clear), never appendable.</summary>
    private static byte[] Encrypted(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = "user";
        doc.SecuritySettings.OwnerPassword = "owner";
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void AnEditThatCantBeAppendedStopsInsteadOfBreakingTheSignatures()
    {
        var pdf = Encrypted(Signed());
        var values = new Dictionary<string, string> { ["Name"] = "Ada" };
        var ex = Assert.Throws<SignaturesWouldBreakException>(() => FormEditor.SetValues(pdf, "owner", values));
        Assert.Equal(1, ex.SignatureCount);
        Assert.Contains("encrypted", ex.Reason);
        Assert.Throws<SignaturesWouldBreakException>(() => PageEditor.Rotate(pdf, "owner", [0], 90));
        Assert.Throws<SignaturesWouldBreakException>(() => SignedEdits.Run(() => Annotations.AddNote(pdf, "owner", 0, 100, 100, "Hi", "Me")));

        // Once the user agreed, the edit is redone as a rewrite.
        var (filled, report) = SignedEdits.Run(() => FormEditor.SetValues(pdf, "owner", values), allowRewrite: true);
        Assert.False(report.Appended);
        Assert.Equal("Ada", FormEditor.Read(filled, "owner").Single(w => w.FieldName == "Name").Value);

        // Explicit rewrites (redaction, extraction) and unsigned files never ask.
        PageEditor.Extract(pdf, "owner", [0]);
        PageEditor.Rotate(TestPdf.Encrypted(2, "pw"), "pw-owner", [0], 90);
    }

    [Fact]
    public void JunkAfterTheEndOfFileStillAppends()
    {
        // More than the 4 KB that used to be searched for the last startxref.
        var junk = "\n" + string.Concat(Enumerable.Repeat("% padding added by a mail gateway\n", 300));
        var pdf = Signed(junk: junk);
        Assert.Equal(SignatureStatus.Valid, Approval(pdf));
        var (filled, report) = SignedEdits.Run(() => FormEditor.SetValues(pdf, null, new Dictionary<string, string> { ["Name"] = "Ada" }));
        Assert.True(report.Appended);
        Assert.True(Appended(pdf, filled));
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Approval(filled));
        Assert.Contains("Ada", Core2Pdf.AllText(filled));
    }

    [Fact]
    public void FindsTheLastValidStartxref()
    {
        var data = Encoding.Latin1.GetBytes("%PDF-1.7\n...startxref\n42\n%%EOF\n" + new string(' ', 5000) + "startxref\nnot a number\n");
        Assert.Equal(42, Incremental.StartXref(data));
        Assert.Null(Incremental.StartXref(Encoding.Latin1.GetBytes("%PDF-1.7\nno trailer")));
    }

    [Theory]
    [InlineData("/ID [<0123ABCD> <89ef>]", "[<0123ABCD> <89ef>]")]
    [InlineData("/ID[<01 23\nAB><4>]", "[<0123AB> <40>]")]
    [InlineData(@"/ID [(abc\)\(d) (\101\x\n)]", "[<616263292864> <41780A>]")]
    [InlineData("/ID [(a(b)c) <FF>] /Size 3", "[<6128622963> <FF>]")]
    [InlineData("/ID [(unterminated", null)]
    [InlineData("/Size 3", null)]
    public void ReadsTheFileIdAsHexOrLiteralStrings(string trailer, string? expected) =>
        Assert.Equal(expected, Incremental.FileId(trailer));

    [Fact]
    public void ALiteralFileIdIsCarriedIntoTheUpdate()
    {
        var pdf = Signed(trailerExtra: @"/ID [(sig\)ned) (id)] ");
        var filled = FormEditor.SetValues(pdf, null, new Dictionary<string, string> { ["Name"] = "Ada" });
        Assert.True(Appended(pdf, filled));
        var update = Encoding.Latin1.GetString(filled, pdf.Length, filled.Length - pdf.Length);
        Assert.Contains($"/ID [<{Convert.ToHexString("sig)ned"u8)}> <{Convert.ToHexString("id"u8)}>]", update);
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Approval(filled));
    }

    [Fact]
    public void RemovalsAppendedToASignedFileAreReportedAndFlattenRemovesThem()
    {
        var pdf = Signed();
        var (deleted, report) = SignedEdits.Run(() => PageEditor.Delete(pdf, null, [1]));
        Assert.Equal(new SignedEditReport(true, true), report);
        Assert.Contains("Second page", Encoding.Latin1.GetString(deleted)); // still in the signed revision
        using (var engine = PdfEngine.Open(deleted, MuPDFCore.InputFileTypes.PDF, null)) Assert.Equal(1, engine.PageCount);

        var flat = SignedEdits.Flatten(deleted, null);
        Assert.DoesNotContain("Second page", PdfText.Everything(flat));
        Assert.Equal("Approval", Assert.Single(Signatures.BrokenByEdits(Signatures.Verify(pdf), flat)).FieldName);

        // Comments and attachments too; adding things isn't a removal.
        var (noted, added) = SignedEdits.Run(() => Annotations.AddNote(pdf, null, 0, 100, 100, "Private remark", "Me"));
        Assert.Equal(new SignedEditReport(true, false), added);
        var id = Annotations.Read(noted, null).Single(a => a.Contents == "Private remark").Id;
        Assert.True(SignedEdits.Run(() => Annotations.Delete(noted, null, id)).Report.RemovedItemKept);
        var attached = Attachments.Add(pdf, null, "secret.txt", "classified"u8.ToArray());
        var key = Attachments.List(attached, null).Single().Key;
        Assert.True(SignedEdits.Run(() => Attachments.Remove(attached, null, key)).Report.RemovedItemKept);
        // Fill & Sign items and headers, footers and watermarks too.
        var typed = TextStamps.Put(pdf, null, 0, new RectD(100, 100, 200, 20), StampKind.Text, "Private remark", 12);
        var stampId = TextStamps.Read(typed, null).Single().Id;
        Assert.True(SignedEdits.Run(() => TextStamps.Remove(typed, null, stampId)).Report.RemovedItemKept);
        var footer = HeaderFooter.Apply(pdf, null, [0], new HeaderFooterOptions { FooterCenter = "Private" });
        Assert.True(SignedEdits.Run(() => HeaderFooter.Remove(footer, null)).Report.RemovedItemKept);
        Assert.False(SignedEdits.Run(() => HeaderFooter.Remove(pdf, null)).Report.RemovedItemKept);

        // Unsigned files are rewritten: nothing is left behind.
        Assert.Equal(new SignedEditReport(false, false), SignedEdits.Run(() => PageEditor.Delete(TestPdf.Make(3), null, [1])).Report);
    }

    [Fact]
    public void AnAppendedEditCopiesTheFileOnce()
    {
        var pdf = Signed(pages: 20, megabytes: 40);
        PageEditor.Rotate(pdf, null, [2], 90); // warm up
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var rotated = PageEditor.Rotate(pdf, null, [2], 90);
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"signed {pdf.Length / 1048576.0:F1} MB: allocated {allocated / 1048576.0:F1} MB ({allocated / (double)pdf.Length:F2}x), {sw.ElapsedMilliseconds} ms");
        Assert.True(Appended(pdf, rotated));
        // Two parses of the original (edit + pairing) and the result. It was 5x before the update was joined once and
        // the result no longer re-read to count its pages.
        Assert.True(allocated < 3.6 * pdf.Length, $"allocated {allocated / (double)pdf.Length:F2}x the file size");
    }
}

/// <summary>Everything a PDF holds as text: its raw bytes and every stream decoded.</summary>
internal static class PdfText
{
    public static string Everything(byte[] pdf)
    {
        var sb = new StringBuilder(Encoding.Latin1.GetString(pdf));
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        foreach (var obj in doc.Internals.GetAllObjects())
            if (obj is PdfSharp.Pdf.PdfDictionary { Stream: { } stream })
                sb.Append('\n').Append(Encoding.Latin1.GetString(stream.UnfilteredValue));
        return sb.ToString();
    }
}
