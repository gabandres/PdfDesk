using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>
/// Later incremental updates are checked against what each signature permits (DocMDP certification, FieldMDP locks, and
/// for approval signatures: nothing that changes the signed pages), and trust doesn't rest on what the signer claims.
/// </summary>
public class SignatureChangeTests
{
    private static SignatureInfo Approval(byte[] pdf) => Signatures.Verify(pdf).Single(s => s.FieldName == "Approval");

    /// <summary>Appends an incremental update (classic xref table) redefining or adding <paramref name="objects"/>.</summary>
    private static byte[] Append(byte[] pdf, IReadOnlyDictionary<int, string> objects, string? root = null)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var prev = Regex.Matches(text, @"startxref\s+(\d+)").Last().Groups[1].Value;
        var size = Regex.Matches(text, @"/Size (\d+)").Max(m => int.Parse(m.Groups[1].Value));
        root ??= Regex.Matches(text, @"/Root (\d+ \d+ R)").Last().Groups[1].Value;
        var sb = new StringBuilder("\n");
        var offsets = new SortedDictionary<int, int>();
        foreach (var (n, body) in objects)
        {
            offsets[n] = pdf.Length + sb.Length;
            sb.Append($"{n} 0 obj\n{body}\nendobj\n");
        }
        var xref = pdf.Length + sb.Length;
        sb.Append("xref\n");
        foreach (var (n, at) in offsets) sb.Append($"{n} 1\n{at:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {Math.Max(size, offsets.Keys.Max() + 1)} /Root {root} /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        return [.. pdf, .. Encoding.Latin1.GetBytes(sb.ToString())];
    }

    /// <summary>The body of object <paramref name="n"/> as it is in the file (between "n 0 obj" and "endobj").</summary>
    private static string Body(byte[] pdf, int n)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var start = text.LastIndexOf($"\n{n} 0 obj", StringComparison.Ordinal) + $"\n{n} 0 obj".Length;
        return text[start..text.IndexOf("endobj", start, StringComparison.Ordinal)].Trim();
    }

    [Fact]
    public void ReplacedPageContentIsNotPermitted()
    {
        // A "shadow attack": the signed content stream is redefined in a later update.
        var pdf = IncrSignatureTests.SignedForm();
        var forged = Append(pdf, new Dictionary<int, string> { [7] = Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Pay 1,000,000) Tj ET") });
        var s = Approval(forged);
        Assert.Equal(SignatureStatus.DisallowedChanges, s.Status);
        Assert.Contains("content of page 1", s.Detail);
        Assert.Contains(Signatures.BrokenByEdits(Signatures.Verify(pdf), forged), x => x.FieldName == "Approval");
    }

    [Fact]
    public void HidingContentWithLayersIsNotPermitted()
    {
        var pdf = IncrSignatureTests.SignedForm();
        var catalog = Body(pdf, 1);
        var hidden = Append(pdf, new Dictionary<int, string>
        {
            [1] = catalog[..^2] + " /OCProperties << /OCGs [20 0 R] /D << /OFF [20 0 R] >> >> >>",
            [20] = "<< /Type /OCG /Name (Hidden) >>",
        });
        var s = Approval(hidden);
        Assert.Equal(SignatureStatus.DisallowedChanges, s.Status);
        Assert.Contains("layers", s.Detail);
    }

    [Fact]
    public void ALaterUpdateCantRewriteTheSignaturesOwnEntries()
    {
        var pdf = IncrSignatureTests.SignedForm();
        var sig = Body(pdf, 5).Replace("/M (D:20260101120000Z)", "/M (D:20990101120000Z) /Reason (Forged)");
        var spoofed = Append(pdf, new Dictionary<int, string> { [5] = sig });
        var s = Approval(spoofed);
        Assert.Equal(SignatureStatus.DisallowedChanges, s.Status);
        Assert.Contains("earlier signature", s.Detail);
        // What is shown is what was signed.
        Assert.Null(s.Reason);
        Assert.Equal(2026, s.SigningTime!.Value.Year);
    }

    [Fact]
    public void NewCommentsAreFineForApprovalButNotUnderFormFillingCertification()
    {
        using var cert = SigningTests.RsaCert();
        var approved = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert });
        var noted = Annotations.AddNote(approved, null, 0, 100, 100, "Looks good", "Reviewer");
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Assert.Single(Signatures.Verify(noted)).Status);

        var certified = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Certify = CertificationLevel.FormFilling });
        var s = Assert.Single(Signatures.Verify(Annotations.AddNote(certified, null, 0, 100, 100, "Looks good", "Reviewer")));
        Assert.Equal(SignatureStatus.DisallowedChanges, s.Status);
        Assert.Equal(2, s.CertificationLevel);
        Assert.Contains("certification", s.Detail);

        var commentsAllowed = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Certify = CertificationLevel.FormFillingAndComments });
        Assert.Equal(SignatureStatus.ModifiedAfterSigning,
            Assert.Single(Signatures.Verify(Annotations.AddNote(commentsAllowed, null, 0, 100, 100, "ok", "Reviewer"))).Status);
    }

    [Fact]
    public void NoChangesCertificationRejectsEvenFormFilling()
    {
        using var cert = SigningTests.RsaCert();
        var form = FormWithEmptySignatureField();
        var locked = Signing.Sign(form, null, new SignRequest { Certificate = cert, Certify = CertificationLevel.NoChanges });
        var filled = FormEditor.SetValues(locked, null, new Dictionary<string, string> { ["Name"] = "Ada" });
        var s = Signatures.Verify(filled).Single(x => x.CertificationLevel == 1);
        Assert.Equal(SignatureStatus.DisallowedChanges, s.Status);
        Assert.Contains("no changes allowed", s.Detail);

        var formFilling = Signing.Sign(form, null, new SignRequest { Certificate = cert, Certify = CertificationLevel.FormFilling });
        var ok = FormEditor.SetValues(formFilling, null, new Dictionary<string, string> { ["Name"] = "Ada" });
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Signatures.Verify(ok).Single(x => x.CertificationLevel == 2).Status);
    }

    [Fact]
    public void FormFillingCertificationAllowsOnlyItsOwnSignatureFields()
    {
        using var cert = SigningTests.RsaCert();
        var certified = Signing.Sign(FormWithEmptySignatureField(), null, new SignRequest { Certificate = cert, Certify = CertificationLevel.FormFilling });
        // A new signature field would break the certification: refused.
        var ex = Assert.Throws<SigningException>(() => Signing.Sign(certified, null, new SignRequest { Certificate = cert }));
        Assert.Contains("existing signature fields", ex.Message);
        // Signing the empty field the certifier left is permitted.
        var signed = Signing.Sign(certified, null, new SignRequest { Certificate = cert, FieldName = "Witness" });
        var all = Signatures.Verify(signed);
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, all.Single(x => x.CertificationLevel == 2).Status);
        Assert.Equal(SignatureStatus.Valid, all.Single(x => x.FieldName == "Witness").Status);
    }

    [Fact]
    public void LockedFieldsAreLockedAndTheirChangesDetected()
    {
        using var cert = SigningTests.RsaCert();
        var form = FormWithEmptySignatureField(lockName: true);
        var filled = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["Name"] = "Ada" });
        var signed = Signing.Sign(filled, null, new SignRequest { Certificate = cert, FieldName = "Witness" });
        var text = Encoding.Latin1.GetString(signed, filled.Length, signed.Length - filled.Length);
        Assert.Contains("/FieldMDP", text);
        Assert.True(FormEditor.Read(signed, null).Single(w => w.FieldName == "Name").ReadOnly);

        // Changing the locked field afterwards (another tool could ignore the read-only flag) is caught.
        int name;
        using (var doc = Core2Pdf.Open(signed))
            name = doc.Internals.GetAllObjects().OfType<PdfSharp.Pdf.PdfDictionary>().Last(d => d.Elements.GetString("/T") == "Name").Reference!.ObjectNumber;
        var body = Regex.Replace(Body(signed, name), @"/V\s*(\([^)]*\)|<[^>]*>)", "/V (Eve)");
        var changed = Append(signed, new Dictionary<int, string> { [name] = body });
        Assert.Equal("Eve", FormEditor.Read(changed, null).Single(w => w.FieldName == "Name").Value);
        var s = Signatures.Verify(changed).Single(x => x.FieldName == "Witness");
        Assert.Equal(SignatureStatus.DisallowedChanges, s.Status);
        Assert.Contains("locked field", s.Detail);
    }

    [Fact]
    public void ACertificateForWebsitesCantSignDocuments()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=www.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // TLS server only
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var signed = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert });
        var s = Assert.Single(Signatures.Verify(signed));
        Assert.Equal(SignatureStatus.Valid, s.Status); // the bytes check out…
        Assert.False(s.CertificateTrusted);            // …but the certificate isn't one for signing documents
        Assert.Contains("isn't meant for signing documents", s.CertificateStatus);
    }

    [Fact]
    public void AnExpiredCertificateIsJudgedNowWithoutATrustedTimestamp()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Expired", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-3), DateTimeOffset.UtcNow.AddYears(-2));
        // Signed "back then" by the signer's own (claimed) clock.
        var signed = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Time = DateTimeOffset.UtcNow.AddYears(-2).AddDays(-30) });
        var s = Assert.Single(Signatures.Verify(signed));
        Assert.True(s.CertificateValidAtSigningTime); // by the claimed time…
        Assert.Contains("NotTimeValid", s.CertificateStatus); // …but that claim doesn't count: the chain is built today
        Assert.Contains("no trusted timestamp", s.CertificateStatus);
    }

    [Fact]
    public async Task ATimestampTakenFromAnotherSignatureIsIgnored()
    {
        // The token sits outside what the signer signed: copy a genuine token from another signature into this one.
        using var cert = SigningTests.RsaCert();
        using var tsa = new FakeTsa();
        var a = await Signing.SignAsync(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Timestamp = tsa.Respond });
        var b = await Signing.SignAsync(TestPdf.Make(2), null, new SignRequest { Certificate = cert, Timestamp = tsa.Respond });
        Assert.True(Assert.Single(Signatures.Verify(a)).HasTimestamp);

        var (aStart, aCms) = Contents(a);
        var (_, bCms) = Contents(b);
        const string tokenOid = "1.2.840.113549.1.9.16.2.14";
        var stolen = bCms.SignerInfos[0].UnsignedAttributes.Cast<System.Security.Cryptography.CryptographicAttributeObject>().Single(x => x.Oid.Value == tokenOid).Values[0];
        var own = aCms.SignerInfos[0].UnsignedAttributes.Cast<System.Security.Cryptography.CryptographicAttributeObject>().Single(x => x.Oid.Value == tokenOid).Values[0];
        aCms.SignerInfos[0].RemoveUnsignedAttribute(own);
        aCms.SignerInfos[0].AddUnsignedAttribute(stolen);
        var forged = a.ToArray();
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(aCms.Encode()));
        forged.AsSpan(aStart, hex.Length).Clear();
        hex.CopyTo(forged, aStart);
        for (var i = aStart + hex.Length; forged[i] != (byte)'>'; i++) forged[i] = (byte)'0';

        var s = Assert.Single(Signatures.Verify(forged));
        Assert.Equal(SignatureStatus.Valid, s.Status); // the signature itself still checks out…
        Assert.False(s.HasTimestamp);                  // …but the transplanted token is ignored
        Assert.Contains("doesn't belong to this signature", s.Detail);

        static (int Start, System.Security.Cryptography.Pkcs.SignedCms Cms) Contents(byte[] pdf)
        {
            var text = Encoding.Latin1.GetString(pdf);
            var start = text.LastIndexOf("/Contents <", StringComparison.Ordinal) + "/Contents <".Length;
            // The zero padding after the CMS can't just be trimmed (the signature may end in zero bytes): read the DER length.
            var bytes = Convert.FromHexString(text[start..text.IndexOf('>', start)]);
            var lengthBytes = bytes[1] & 0x7F;
            var length = bytes[1] < 0x80 ? bytes[1] : bytes.AsSpan(2, lengthBytes).ToArray().Aggregate(0, (n, b) => (n << 8) | b);
            var header = bytes[1] < 0x80 ? 2 : 2 + lengthBytes;
            var cms = new System.Security.Cryptography.Pkcs.SignedCms();
            cms.Decode(bytes.AsSpan(0, header + length));
            return (start, cms);
        }
    }

    [Fact]
    public void PersonalIdBackupUsesModernEncryption()
    {
        var pfx = DigitalIds.CreatePersonalPfx("Ada Lovelace", "ada@example.com", null, "correct horse battery", 1);
        var pfxText = Convert.ToHexString(pfx);
        Assert.Contains("2A864886F70D01050D", pfxText); // PBES2
        Assert.DoesNotContain("2A864886F70D010C0103", pfxText); // pbeWithSHAAnd3-KeyTripleDES-CBC
    }

    /// <summary>A one-page form: text field "Name", an empty signature field "Witness" (optionally locking "Name").</summary>
    private static byte[] FormWithEmptySignatureField(bool lockName = false)
    {
        var lockEntry = lockName ? " /Lock << /Type /SigFieldLock /Action /Include /Fields [(Name)] >>" : "";
        return Core2Pdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 7 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [72 500 300 530] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Witness) /Rect [72 400 272 450] /P 3 0 R /F 4{lockEntry} >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Form) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ]);
    }
}
