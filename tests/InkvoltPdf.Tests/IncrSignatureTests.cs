using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Tests;

/// <summary>Edits of signed documents are saved as incremental updates, so the signatures keep verifying.</summary>
public class IncrSignatureTests
{
    internal const int ContentsHexLength = 16384;

    /// <summary>
    /// A signed form: a signature field ("Approval", signed), a text field ("Name"), optionally a second page. With
    /// <paramref name="xrefStream"/> the file uses a cross-reference stream and keeps the text field and the font in an
    /// object stream.
    /// </summary>
    internal static byte[] SignedForm(bool xrefStream = false, bool twoPages = false)
    {
        var placeholderRange = "[0 0000000000 0000000000 0000000000]";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 6 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> /SigFlags 3 >> >>",
            twoPages ? "<< /Type /Pages /Kids [3 0 R 9 0 R] /Count 2 >>" : "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 6 0 R] /Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approval) /Rect [72 650 272 700] /V 5 0 R /P 3 0 R /F 4 >>",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange {placeholderRange} /Contents <{new string('0', ContentsHexLength)}> /M (D:20260101120000Z) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [72 500 300 530] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Hello contract) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (twoPages)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 10 0 R /Resources << /Font << /F1 8 0 R >> >> >>");
            objects.Add(Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Second page) Tj ET"));
        }
        var pdf = xrefStream ? BuildWithXrefStream(objects, [6, 8]) : Core2Pdf.Build(objects);
        return Sign(pdf, placeholderRange);
    }

    /// <summary>Objects 1..n; <paramref name="compressed"/> go into an object stream; a cross-reference stream at the end.</summary>
    private static byte[] BuildWithXrefStream(IReadOnlyList<string> objects, int[] compressed)
    {
        var n = objects.Count;
        var objStm = n + 1;
        var xref = n + 2;
        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new Dictionary<int, int>();
        for (var i = 1; i <= n; i++)
        {
            if (compressed.Contains(i)) continue;
            offsets[i] = sb.Length;
            sb.Append($"{i} 0 obj\n{objects[i - 1]}\nendobj\n");
        }
        var header = new StringBuilder();
        var body = new StringBuilder();
        foreach (var c in compressed)
        {
            header.Append($"{c} {body.Length} ");
            body.Append(objects[c - 1]).Append('\n');
        }
        var content = header.ToString() + body;
        offsets[objStm] = sb.Length;
        sb.Append($"{objStm} 0 obj\n<< /Type /ObjStm /N {compressed.Length} /First {header.Length} /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[xref] = sb.Length;
        var rows = new List<byte>();
        for (var i = 0; i <= xref; i++)
        {
            if (i == 0) rows.AddRange([0, 0, 0, 0, 0, 0xFF, 0xFF]);
            else if (compressed.Contains(i)) rows.AddRange([2, .. Be(objStm, 4), .. Be(Array.IndexOf(compressed, i), 2)]);
            else rows.AddRange([1, .. Be(offsets[i], 4), 0, 0]);
        }
        sb.Append($"{xref} 0 obj\n<< /Type /XRef /Size {xref + 1} /W [1 4 2] /Root 1 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] /Length {rows.Count} >>\nstream\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        var tail = Encoding.Latin1.GetBytes($"\nendstream\nendobj\nstartxref\n{offsets[xref]}\n%%EOF\n");
        return [.. head, .. rows, .. tail];

        static byte[] Be(int v, int width) => [.. Enumerable.Range(0, width).Select(k => (byte)(v >> (8 * (width - 1 - k))))];
    }

    internal static byte[] Sign(byte[] pdf, string placeholderRange)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var gapStart = text.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        var gapEnd = text.IndexOf('>', gapStart) + 1;
        var at = text.IndexOf(placeholderRange, StringComparison.Ordinal);
        Encoding.Latin1.GetBytes($"[0 {gapStart:D10} {gapEnd:D10} {pdf.Length - gapEnd:D10}]").CopyTo(pdf, at);
        var signedBytes = pdf[..gapStart].Concat(pdf[gapEnd..]).ToArray();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Incremental Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") });
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms.Encode())).CopyTo(pdf, gapStart + 1);
        return pdf;
    }

    private static SignatureInfo Approval(byte[] pdf) => Signatures.Verify(pdf).Single(s => s.FieldName == "Approval");

    /// <summary>
    /// The edit kept the signed bytes as they were and the signature still checks out, with later changes: permitted ones
    /// (form filling, comments, metadata), or, with <paramref name="permitted"/> false, ones that change the signed pages.
    /// </summary>
    private static void AssertAppended(byte[] original, byte[] edited, bool permitted = true)
    {
        Assert.Equal(SignatureStatus.Valid, Approval(original).Status);
        Assert.True(edited.Length > original.Length);
        Assert.True(edited.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes must be kept as they are");
        var s = Approval(edited);
        Assert.Equal(permitted ? SignatureStatus.ModifiedAfterSigning : SignatureStatus.DisallowedChanges, s.Status);
        Assert.False(s.CoversWholeDocument);
        Assert.Equal("Incremental Signer", s.SignerName);
        if (permitted) Assert.Empty(Signatures.BrokenByEdits(Signatures.Verify(original), edited));
        else Assert.Contains("page", s.Detail);
        // Unchanged objects (the signed page content) aren't repeated in the update.
        Assert.DoesNotContain("Hello contract", Encoding.Latin1.GetString(edited, original.Length, edited.Length - original.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormFillIsAppended(bool xrefStream)
    {
        var pdf = SignedForm(xrefStream);
        var filled = FormEditor.SetValues(pdf, null, new Dictionary<string, string> { ["Name"] = "Ada Lovelace" });
        AssertAppended(pdf, filled);
        var update = Encoding.Latin1.GetString(filled, pdf.Length, filled.Length - pdf.Length);
        Assert.Contains(xrefStream ? "/Type /XRef" : "\nxref\n", update);
        var text = Encoding.Latin1.GetString(pdf);
        var prev = text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref".Length)..].Trim().Split('\n')[0].Trim();
        Assert.Contains($"/Prev {prev}", update);
        Assert.Equal("Ada Lovelace", FormEditor.Read(filled, null).Single(w => w.FieldName == "Name").Value);
        Assert.Contains("Ada Lovelace", Core2Pdf.AllText(filled));
        Assert.Contains("Hello contract", Core2Pdf.AllText(filled));
        if (xrefStream) Assert.Contains("/ID [<0123456789ABCDEF0123456789ABCDEF>", update);

        // A second edit appends another update after the first.
        var again = FormEditor.SetValues(filled, null, new Dictionary<string, string> { ["Name"] = "Grace Hopper" });
        Assert.True(again.AsSpan(0, filled.Length).SequenceEqual(filled));
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Approval(again).Status);
        Assert.Contains("Grace Hopper", Core2Pdf.AllText(again));
        Assert.DoesNotContain("Ada Lovelace", Core2Pdf.AllText(again));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FillAndSignTextIsAppended(bool xrefStream)
    {
        var pdf = SignedForm(xrefStream);
        var stamped = TextStamps.Put(pdf, null, 0, new RectD(72, 300, 200, 20), StampKind.Text, "Received", 12);
        AssertAppended(pdf, stamped);
        Assert.Contains("Received", Core2Pdf.AllText(stamped));
        Assert.Single(TextStamps.Read(stamped, null));
        var removed = TextStamps.Remove(stamped, null, TextStamps.Read(stamped, null)[0].Id);
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Approval(removed).Status);
        Assert.Empty(TextStamps.Read(removed, null));
    }

    [Fact]
    public void CommentsAttachmentsAndMetadataAreAppended()
    {
        var pdf = SignedForm();
        var noted = Annotations.AddNote(pdf, null, 0, 400, 100, "Checked", "Reviewer");
        AssertAppended(pdf, noted);
        Assert.Contains(Annotations.Read(noted, null), a => a.Contents == "Checked");

        var attached = Attachments.Add(noted, null, "notes.txt", Encoding.UTF8.GetBytes("hello"));
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Approval(attached).Status);
        Assert.Contains(Attachments.List(attached, null), a => a.FileName == "notes.txt");

        var titled = PageEditor.WriteMetadata(attached, null, "Contract", "Ada", "", "");
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Approval(titled).Status);
        Assert.Equal("Contract", PageEditor.ReadMetadata(titled, null).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageRotateAndDeleteAreAppendedButNotPermitted(bool xrefStream)
    {
        // The signature stays intact, but rotating or removing a signed page changes what was signed.
        var pdf = SignedForm(xrefStream, twoPages: true);
        var rotated = PageEditor.Rotate(pdf, null, [1], 90);
        AssertAppended(pdf, rotated, permitted: false);
        Assert.Contains("page 2", Approval(rotated).Detail);
        using (var doc = PdfReader.Open(new MemoryStream(rotated), PdfDocumentOpenMode.Import))
        {
            Assert.Equal(0, doc.Pages[0].Rotate);
            Assert.Equal(90, doc.Pages[1].Rotate);
        }
        using (var engine = PdfEngine.Open(rotated, InputFileTypes.PDF, null))
        {
            Assert.Equal(2, engine.PageCount);
            var size = engine.GetPage(1);
            Assert.True(size.Width > size.Height, "MuPDF shows page 2 rotated");
        }

        var deleted = PageEditor.Delete(pdf, null, [1]);
        AssertAppended(pdf, deleted, permitted: false);
        Assert.Contains("pages added, removed or moved", Approval(deleted).Detail);
        using (var engine = PdfEngine.Open(deleted, InputFileTypes.PDF, null)) Assert.Equal(1, engine.PageCount);
        Assert.Contains("Hello contract", Core2Pdf.AllText(deleted));
    }

    /// <summary>A signed one-page file with a nested page tree (inherited /MediaBox), an Info dictionary and an XMP packet.</summary>
    internal static byte[] SignedNested()
    {
        var placeholderRange = "[0 0000000000 0000000000 0000000000]";
        var xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Original</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 10 0 R /AcroForm << /Fields [4 0 R 6 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [11 0 R] /Count 1 /MediaBox [0 0 400 500] >>",
            "<< /Type /Page /Parent 11 0 R /Annots [4 0 R 6 0 R] /Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approval) /Rect [72 650 272 700] /V 5 0 R /P 3 0 R /F 4 >>",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange {placeholderRange} /Contents <{new string('0', ContentsHexLength)}> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [72 500 300 530] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Hello contract) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Title (Original) /Producer (Test) >>",
            Core2Pdf.Stream(xmp, "/Type /Metadata /Subtype /XML"),
            "<< /Type /Pages /Parent 2 0 R /Kids [3 0 R] /Count 1 >>",
        };
        return Sign(Core2Pdf.Build(objects, "/Info 9 0 R "), placeholderRange);
    }

    [Fact]
    public void NestedPageTreeInfoAndXmpSurviveAppendedEdits()
    {
        var pdf = SignedNested();
        var filled = FormEditor.SetValues(pdf, null, new Dictionary<string, string> { ["Name"] = "Ada" });
        AssertAppended(pdf, filled);
        Assert.Contains("Ada", Core2Pdf.AllText(filled));

        var rotated = PageEditor.Rotate(filled, null, [0], 180);
        Assert.Equal(SignatureStatus.DisallowedChanges, Approval(rotated).Status);
        using (var engine = PdfEngine.Open(rotated, InputFileTypes.PDF, null))
        {
            Assert.Equal(1, engine.PageCount);
            Assert.Equal(400, engine.GetPage(0).Width, 1);
        }
        Assert.Contains("Hello contract", Core2Pdf.AllText(rotated));

        // Adding a page rewrites the (flattened) page tree; the old page keeps its inherited size.
        var grown = PageEditor.InsertBlank(rotated, null, 1, 1, 300, 300);
        Assert.Equal(SignatureStatus.DisallowedChanges, Approval(grown).Status);
        using (var engine = PdfEngine.Open(grown, InputFileTypes.PDF, null))
        {
            Assert.Equal(2, engine.PageCount);
            Assert.Equal((400, 500), (engine.GetPage(0).Width, engine.GetPage(0).Height));
            Assert.Equal(300, engine.GetPage(1).Width, 1);
        }
        var shrunk = PageEditor.Delete(grown, null, [1]);
        Assert.Equal(SignatureStatus.DisallowedChanges, Approval(shrunk).Status);
        using (var engine = PdfEngine.Open(shrunk, InputFileTypes.PDF, null)) Assert.Equal(1, engine.PageCount);

        var titled = PageEditor.WriteMetadata(filled, null, "Signed contract", "", "", "");
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Approval(titled).Status);
        Assert.Equal("Signed contract", PageEditor.ReadMetadata(titled, null).Title);
        using var doc = PdfReader.Open(new MemoryStream(titled), PdfDocumentOpenMode.Import);
        var meta = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Internals.Catalog.Elements["/Metadata"]!).Value;
        Assert.Contains("Signed contract", Encoding.UTF8.GetString(meta.Stream.UnfilteredValue));
    }

    [Fact]
    public void RedactionAndExtractionRewriteTheFile()
    {
        var pdf = SignedForm(twoPages: true);
        var original = Signatures.Verify(pdf);
        var redacted = Redaction.Apply(pdf, null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [new RectD(72, 60, 100, 20)] }, 72);
        Assert.False(redacted.AsSpan(0, Math.Min(pdf.Length, redacted.Length)).SequenceEqual(pdf.AsSpan(0, Math.Min(pdf.Length, redacted.Length))));
        Assert.Equal("Approval", Assert.Single(Signatures.BrokenByEdits(original, redacted)).FieldName);

        var extracted = PageEditor.Extract(pdf, null, [1]);
        Assert.True(extracted.Length < pdf.Length);
    }

    [Fact]
    public void UnsignedFilesAreStillRewritten()
    {
        var pdf = Core2Pdf.Form(["<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [72 500 300 530] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>"]);
        var filled = FormEditor.SetValues(pdf, null, new Dictionary<string, string> { ["Name"] = "Ada" });
        Assert.False(filled.Length > pdf.Length && filled.AsSpan(0, pdf.Length).SequenceEqual(pdf));
    }
}
