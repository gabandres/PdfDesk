using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class Core2SignatureTests
{
    private const int ContentsHexLength = 16384;

    /// <summary>A one-page PDF signed (adbe.pkcs7.detached, SHA-256) by a self-signed certificate, plus an empty signature field.</summary>
    internal static byte[] Signed(string subFilter = "adbe.pkcs7.detached")
    {
        var placeholderRange = "[0 0000000000 0000000000 0000000000]";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 6 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 6 0 R] /Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approval) /Rect [72 650 272 700] /V 5 0 R /P 3 0 R /F 4 >>",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /{subFilter} /ByteRange {placeholderRange} /Contents <{new string('0', ContentsHexLength)}> /M (D:20260101120000Z) /Reason (Approved) /Location (San Juan) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Witness) /Rect [300 650 500 700] /P 3 0 R /F 4 >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Hello contract) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = Core2Pdf.Build(objects);
        var text = Encoding.Latin1.GetString(pdf);
        var gapStart = text.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        var gapEnd = text.IndexOf('>', gapStart) + 1;
        var range = $"[0 {gapStart:D10} {gapEnd:D10} {pdf.Length - gapEnd:D10}]";
        text = text.Replace(placeholderRange, range);
        pdf = Encoding.Latin1.GetBytes(text);

        var signedBytes = pdf[..gapStart].Concat(pdf[gapEnd..]).ToArray();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Signer, O=Inkvolt PDF Tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        SignedCms cms;
        if (subFilter == "adbe.pkcs7.sha1")
            cms = new SignedCms(new ContentInfo(SHA1.HashData(signedBytes)), detached: false);
        else
            cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        cms.ComputeSignature(signer);
        var hex = Convert.ToHexString(cms.Encode());
        Assert.True(hex.Length <= ContentsHexLength);
        Encoding.ASCII.GetBytes(hex).CopyTo(pdf, gapStart + 1);
        return pdf;
    }

    [Theory]
    [InlineData("adbe.pkcs7.detached")]
    [InlineData("ETSI.CAdES.detached")]
    [InlineData("adbe.pkcs7.sha1")]
    public void ValidSignatureVerifies(string subFilter)
    {
        var sigs = Signatures.Verify(Signed(subFilter));
        Assert.Equal(2, sigs.Count);
        var s = sigs.Single(x => x.FieldName == "Approval");
        Assert.True(s.Status == SignatureStatus.Valid, s.Detail);
        Assert.Equal("Test Signer", s.SignerName);
        Assert.Equal("Approved", s.Reason);
        Assert.Equal("San Juan", s.Location);
        Assert.Equal(0, s.PageIndex);
        Assert.Equal(new RectD(72, 92, 200, 50), s.Rect);
        Assert.True(s.CoversWholeDocument);
        Assert.NotNull(s.SigningTime);
        Assert.True(s.CertificateValidAtSigningTime);
        Assert.False(s.CertificateTrusted); // self-signed: not chained to a Windows root
        Assert.NotEqual("OK", s.CertificateStatus);
        Assert.Equal(SignatureStatus.Unsigned, sigs.Single(x => x.FieldName == "Witness").Status);
    }

    [Fact]
    public void EditsThatBreakASignatureAreReportedAgainstTheOpenedFile()
    {
        var pdf = Signed();
        var original = Signatures.Verify(pdf);
        Assert.Empty(Signatures.BrokenByEdits(original, pdf));
        // Most edits are appended (the signature stays intact); redaction rewrites the file. Rotating a signed page is
        // appended too, but it changes what was signed.
        Assert.Empty(Signatures.BrokenByEdits(original, Annotations.AddNote(pdf, null, 0, 300, 100, "ok", "me")));
        var rotated = Assert.Single(Signatures.BrokenByEdits(original, PageEditor.Rotate(pdf, null, [0], 90)));
        Assert.Equal("Approval", rotated.FieldName);
        var edited = Redaction.Apply(pdf, null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [new RectD(72, 60, 100, 20)] }, 72);
        var broken = Signatures.BrokenByEdits(original, edited);
        Assert.Equal("Approval", Assert.Single(broken).FieldName); // the unsigned "Witness" field isn't reported
    }

    [Fact]
    public void ChangedBytesMakeItInvalid()
    {
        var pdf = Signed();
        var at = Encoding.Latin1.GetString(pdf).IndexOf("Hello contract", StringComparison.Ordinal);
        pdf[at] = (byte)'J';
        var s = Signatures.Verify(pdf).Single(x => x.FieldName == "Approval");
        Assert.Equal(SignatureStatus.Invalid, s.Status);
        Assert.NotNull(s.Detail);
    }

    [Fact]
    public void LaterIncrementalUpdatesAreReported()
    {
        var pdf = Signed();
        var text = Encoding.Latin1.GetString(pdf);
        var prev = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        var sb = new StringBuilder(text);
        var objAt = sb.Length;
        sb.Append("9 0 obj\n<< /Title (Changed later) >>\nendobj\n");
        var xref = sb.Length;
        sb.Append($"xref\n9 1\n{objAt:D10} 00000 n \ntrailer\n<< /Size 10 /Root 1 0 R /Info 9 0 R /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        var s = Signatures.Verify(Encoding.Latin1.GetBytes(sb.ToString())).Single(x => x.FieldName == "Approval");
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, s.Status);
        Assert.False(s.CoversWholeDocument);
    }

    [Fact]
    public void ContentSlippedIntoTheSignaturePaddingIsNotValid()
    {
        var pdf = Signed();
        var text = Encoding.Latin1.GetString(pdf);
        var gapEnd = text.IndexOf('>', text.IndexOf("/Contents <", StringComparison.Ordinal));
        var junk = ">\n9 0 obj\n<< /Unsigned (evil) >>\nendobj\n<";
        Encoding.Latin1.GetBytes(junk).CopyTo(pdf, gapEnd - junk.Length - 2); // inside the zero padding
        var sigs = Signatures.Verify(pdf);
        Assert.All(sigs, s => Assert.NotEqual(SignatureStatus.Valid, s.Status));
        Assert.Contains(sigs, s => s.Status == SignatureStatus.Invalid);
    }

    [Theory]
    [InlineData("9223372036854775000", "9223372036854775000")]
    [InlineData("99999999999999999999999", "5")]
    [InlineData("-5", "10")]
    [InlineData("1.5", "2")]
    public void HostileByteRangesNeverThrowNorValidate(string second, string fourth)
    {
        var text = Encoding.Latin1.GetString(Signed());
        var start = text.IndexOf("/ByteRange [", StringComparison.Ordinal);
        var end = text.IndexOf(']', start);
        var parts = text[(start + 12)..end].Split(' ');
        // Same length when it fits (the xref stays valid and PDFsharp parses the file), else the raw-scan fallback runs.
        var range = $"/ByteRange [0 {second} {parts[2]} {fourth}".PadRight(end - start);
        text = text[..start] + range + text[end..];
        var sigs = Signatures.Verify(Encoding.Latin1.GetBytes(text));
        Assert.All(sigs, s => Assert.NotEqual(SignatureStatus.Valid, s.Status));
        Assert.Contains(sigs, s => s.Status == SignatureStatus.Invalid);
    }

    [Fact]
    public void UnsupportedFormatsAndUnsignedFiles()
    {
        var pdf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Signed()).Replace("/adbe.pkcs7.detached", "/adbe.x509.rsa_sha1 "));
        Assert.Equal(SignatureStatus.Unsupported, Signatures.Verify(pdf).Single(x => x.FieldName == "Approval").Status);
        Assert.Empty(Signatures.Verify(TestPdf.Make(1)));
    }
}
