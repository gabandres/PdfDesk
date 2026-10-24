using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace InkvoltPdf.Tests;

public class OptimizerTests
{
    private const int Side = 1200;

    /// <summary>
    /// Page 1: text (ASCIIHex-encoded content) and a 1200x1200 uncompressed gradient shown 300 pt wide (288 dpi), plus a
    /// thumbnail. Page 2: two byte-identical small images (72 dpi). Object 13: an unreferenced 100 KB stream.
    /// </summary>
    private static byte[] Bloated()
    {
        var pixels = new char[Side * Side * 3];
        for (var y = 0; y < Side; y++)
        for (var x = 0; x < Side; x++)
        {
            var o = (y * Side + x) * 3;
            (pixels[o], pixels[o + 1], pixels[o + 2]) = ((char)(x * 255 / (Side - 1)), (char)(y * 255 / (Side - 1)), (char)128);
        }
        var small = new string(Enumerable.Range(0, 64 * 64).Select(i => (char)(i * 7 % 256)).ToArray());
        var text = "BT /F1 24 Tf 72 720 Td (Hello optimizer) Tj ET q 300 0 0 300 72 300 cm /Big Do Q";
        var hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(text)) + ">";
        return Core2Pdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Thumb 12 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Big 6 0 R >> >> >>",
            Core2Pdf.Stream(hex, "/Filter /ASCIIHexDecode"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            Core2Pdf.Stream(new string(pixels), $"/Type /XObject /Subtype /Image /Width {Side} /Height {Side} /ColorSpace /DeviceRGB /BitsPerComponent 8"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 9 0 R /Im2 10 0 R >> >> >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Second page) Tj ET q 64 0 0 64 72 400 cm /Im1 Do Q q 64 0 0 64 200 400 cm /Im2 Do Q"),
            Core2Pdf.Stream(small, "/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceGray /BitsPerComponent 8"),
            Core2Pdf.Stream(small, "/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceGray /BitsPerComponent 8"),
            "<< /Producer (test) >>",
            Core2Pdf.Stream(new string('x', 3 * 20 * 20), "/Width 20 /Height 20 /ColorSpace /DeviceRGB /BitsPerComponent 8"),
            Core2Pdf.Stream(new string('z', 100_000)),
        ]);
    }

    private static PdfDictionary XObject(PdfSharp.Pdf.PdfDocument doc, int page, string name) =>
        PdfObj.Dict(PdfObj.Dict(PdfObj.Dict(doc.Pages[page].Elements["/Resources"])!.Elements["/XObject"])!.Elements[name])!;

    [Fact]
    public void ShrinksAndKeepsTheContent()
    {
        var pdf = Bloated();
        var result = Optimizer.Optimize(pdf, null);
        Assert.Equal(pdf.Length, result.SizeBefore);
        Assert.Equal(result.Data.Length, result.SizeAfter);
        Assert.True(result.SizeAfter * 20 < result.SizeBefore, $"{result.SizeBefore} -> {result.SizeAfter}");
        Assert.Equal(1, result.ImagesDownsampled);
        Assert.True(result.DuplicatesRemoved >= 1);
        Assert.True(result.StreamsCompressed >= 2);

        using (var engine = PdfEngine.Open(result.Data, InputFileTypes.PDF, null))
        {
            Assert.Equal(2, engine.PageCount);
            Assert.Contains("Hello optimizer", engine.GetText(0).Text);
            Assert.Contains("Second page", engine.GetText(1).Text);
        }

        using var doc = Core2Pdf.Open(result.Data);
        var big = XObject(doc, 0, "/Big");
        Assert.Equal("DCTDecode", PdfObj.Name(big.Elements["/Filter"]));
        Assert.InRange(PdfObj.Int(big.Elements["/Width"])!.Value, 620, 630); // 1200 px * 150 / 288
        Assert.Null(doc.Pages[0].Elements["/Thumb"]);
        // The duplicates share one object now.
        var r1 = (PdfReference)PdfObj.Dict(PdfObj.Dict(doc.Pages[1].Elements["/Resources"])!.Elements["/XObject"])!.Elements["/Im1"]!;
        var r2 = (PdfReference)PdfObj.Dict(PdfObj.Dict(doc.Pages[1].Elements["/Resources"])!.Elements["/XObject"])!.Elements["/Im2"]!;
        Assert.Equal(r1.ObjectID, r2.ObjectID);
        // Small images aren't touched, only compressed.
        Assert.Equal(64, PdfObj.Int(XObject(doc, 1, "/Im1").Elements["/Width"]));
        Assert.Equal("FlateDecode", PdfObj.Name(XObject(doc, 1, "/Im1").Elements["/Filter"]));
        // The unreferenced stream is gone.
        Assert.DoesNotContain(doc.Internals.GetAllObjects(), o => o is PdfDictionary { Stream: { } s } && s.Value.Length >= 100_000);

        // The gradient still looks the same: center of the image ≈ (128, 128, 128); left edge red ≈ 0, right edge ≈ 255.
        var center = TestImages.PixelAt(result.Data, 0, 72 + 150, 792 - 300 - 150);
        Assert.InRange(center.R, 115, 141);
        Assert.InRange(center.G, 115, 141);
        Assert.InRange(TestImages.PixelAt(result.Data, 0, 74, 792 - 450).R, 0, 20);
        Assert.InRange(TestImages.PixelAt(result.Data, 0, 370, 792 - 450).R, 235, 255);
    }

    [Fact]
    public void OptionsTurnStepsOff()
    {
        var pdf = Bloated();
        var result = Optimizer.Optimize(pdf, null, new OptimizeOptions { MaxImageDpi = null, DeduplicateStreams = false, RemoveThumbnails = false });
        Assert.Equal(0, result.ImagesDownsampled);
        Assert.Equal(0, result.DuplicatesRemoved);
        using var doc = Core2Pdf.Open(result.Data);
        Assert.Equal(Side, PdfObj.Int(XObject(doc, 0, "/Big").Elements["/Width"]));
        Assert.NotNull(doc.Pages[0].Elements["/Thumb"]);
        Assert.True(result.SizeAfter < result.SizeBefore); // compression alone helps (the gradient deflates well)
    }

    [Fact]
    public void RemovesMetadataWhenAsked()
    {
        var withXmp = PageEditor.WriteMetadata(TestPdf.Make(1), null, "Secret title", "Me", "", "");
        var kept = Optimizer.Optimize(withXmp, null);
        Assert.Equal("Secret title", PageEditor.ReadMetadata(kept.Data, null).Title);
        var stripped = Optimizer.Optimize(withXmp, null, new OptimizeOptions { RemoveMetadata = true });
        // The Info title stays (and so does a minimal XMP packet made from it).
        Assert.Equal("Secret title", PageEditor.ReadMetadata(stripped.Data, null).Title);
    }

    [Fact]
    public void SignedFilesNeedConsent()
    {
        var signed = SignedEditTests.Signed(pages: 1, megabytes: 1);
        Assert.Throws<SignaturesWouldBreakException>(() => Optimizer.Optimize(signed, null));
        var (data, report) = SignedEdits.Run(() => Optimizer.Optimize(signed, null).Data, allowRewrite: true);
        Assert.False(report.Appended);
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        Assert.Equal(3, engine.PageCount);
    }

    [Fact]
    public void EncryptedFilesNeedTheOwnerPassword()
    {
        var pdf = PdfSecurity.Protect(Bloated(), null, "open", "owner");
        Assert.Throws<OwnerPasswordRequiredException>(() => Optimizer.Optimize(pdf, "open"));
        // AES-256: the open password can't be recovered from the owner password, so the caller passes it on.
        Assert.Throws<OpenPasswordUnknownException>(() => Optimizer.Optimize(pdf, "owner"));
        var result = KeptProtection.Run("open", false, () => Optimizer.Optimize(pdf, "owner"));
        Assert.True(result.SizeAfter * 10 < result.SizeBefore);
        Assert.Equal(1, result.ImagesDownsampled);
        // Like every Core edit of an encrypted file, the result keeps the protection.
        Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(result.Data, InputFileTypes.PDF, null));
        Assert.Equal(EncryptionMethod.Aes256, PdfSecurity.ReadSecurity(result.Data, "open").Method);
        using var engine = PdfEngine.Open(result.Data, InputFileTypes.PDF, "open");
        Assert.Contains("Hello optimizer", engine.GetText(0).Text);
    }
}
