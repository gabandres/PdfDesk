using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>Digital signing (Signing, DigitalIds) and handwritten signature stamps, with in-memory certificates only.</summary>
public class SigningTests
{
    internal static X509Certificate2 RsaCert(string cn = "Test Signer")
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 EcCert(ECCurve curve, string cn = "EC Signer")
    {
        using var ec = ECDsa.Create(curve);
        var req = new CertificateRequest($"CN={cn}", ec, curve.Oid.FriendlyName == "nistP384" ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    internal static StampImage Squiggle(int w = 120, int h = 40)
    {
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (var x = 0; x < w; x++)
        {
            var y0 = (int)(h / 2 + Math.Sin(x / 8.0) * h / 3);
            for (var y = Math.Max(0, y0 - 2); y < Math.Min(h, y0 + 3); y++)
            {
                rgb[(y * w + x) * 3 + 2] = 120; // dark blue ink
                alpha[y * w + x] = 255;
            }
        }
        return new StampImage(w, h, rgb, alpha);
    }

    private static SignatureInfo Only(byte[] pdf) => Assert.Single(Signatures.Verify(pdf));

    [Fact]
    public void Invisible_rsa_signature_is_appended_and_valid()
    {
        var original = TestPdf.Make(2);
        using var cert = RsaCert();
        var signed = Signing.Sign(original, null, new SignRequest { Certificate = cert, Reason = "Approved", Location = "San Juan" });

        Assert.True(signed.AsSpan(0, original.Length).SequenceEqual(original), "signing appends an incremental update");
        var s = Only(signed);
        Assert.Equal(SignatureStatus.Valid, s.Status);
        Assert.True(s.CoversWholeDocument);
        Assert.Equal("Test Signer", s.SignerName);
        Assert.Equal("ETSI.CAdES.detached", s.SubFilter);
        Assert.Equal("Approved", s.Reason);
        Assert.Equal("San Juan", s.Location);
        Assert.False(s.HasTimestamp);
        Assert.NotNull(s.SigningTime);
        Assert.True(s.Rect.IsEmpty);

        // PAdES: signing-certificate-v2 signed, no signing-time attribute.
        var cms = Cms(signed);
        var attrs = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToList();
        Assert.Contains(Signing.SigningCertificateV2Oid, attrs);
        Assert.DoesNotContain("1.2.840.113549.1.9.5", attrs);
        Assert.Equal(TestPdf.PageNumbers(original), TestPdf.PageNumbers(signed));
    }

    [Fact]
    public void Visible_ec_signature_with_image_renders()
    {
        var original = TestPdf.Make(1);
        using var cert = EcCert(ECCurve.NamedCurves.nistP384, "Ada Lovelace");
        var rect = new RectD(300, 600, 240, 70);
        var signed = Signing.Sign(original, null, new SignRequest
        {
            Certificate = cert,
            PageIndex = 0,
            Rect = rect,
            Reason = "I agree",
            Look = new SignatureLook { Image = Squiggle(), ShowDistinguishedName = true },
        });
        var s = Only(signed);
        Assert.Equal(SignatureStatus.Valid, s.Status);
        Assert.Equal("Ada Lovelace", s.SignerName);
        Assert.Equal(0, s.PageIndex);
        Assert.InRange(s.Rect.X, rect.X - 0.5, rect.X + 0.5);
        Assert.InRange(s.Rect.Y, rect.Y - 0.5, rect.Y + 0.5);
        Assert.Equal("2.16.840.1.101.3.4.2.2", Cms(signed).SignerInfos[0].DigestAlgorithm.Value); // SHA-384 for P-384

        Assert.Contains("Digitally signed by Ada Lovelace", Core2Pdf.AllText(signed));
        Assert.Contains("Reason: I agree", Core2Pdf.AllText(signed));
        // The image half (right) has ink too.
        Assert.True(Core2Pdf.Ink(signed, 0, new RectD(rect.X + rect.Width / 2, rect.Y, rect.Width / 2, rect.Height)) > 0.01);
    }

    [Fact]
    public void Existing_empty_field_is_signed()
    {
        var form = Core2Pdf.Form(["<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approval) /Rect [72 600 272 650] /P 3 0 R /F 4 >>"]);
        Assert.Equal(SignatureStatus.Unsigned, Only(form).Status);
        using var cert = RsaCert();
        var signed = Signing.Sign(form, null, new SignRequest { Certificate = cert, FieldName = "Approval" });
        var s = Only(signed);
        Assert.Equal("Approval", s.FieldName);
        Assert.Equal(SignatureStatus.Valid, s.Status);
        Assert.Equal(0, s.PageIndex);
        Assert.Contains("Digitally signed by Test Signer", Core2Pdf.AllText(signed));

        var again = Assert.Throws<SigningException>(() => Signing.Sign(signed, null, new SignRequest { Certificate = cert, FieldName = "Approval" }));
        Assert.Contains("already signed", again.Message);
        Assert.Throws<SigningException>(() => Signing.Sign(form, null, new SignRequest { Certificate = cert, FieldName = "Missing" }));
    }

    [Fact]
    public void Second_signature_keeps_the_first_intact()
    {
        using var first = RsaCert("First Signer");
        using var second = EcCert(ECCurve.NamedCurves.nistP256, "Second Signer");
        var once = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = first, Rect = new RectD(72, 600, 200, 50) });
        var twice = Signing.Sign(once, null, new SignRequest { Certificate = second, Rect = new RectD(320, 600, 200, 50) });

        Assert.True(twice.AsSpan(0, once.Length).SequenceEqual(once));
        var all = Signatures.Verify(twice);
        Assert.Equal(2, all.Count);
        var a = all.Single(x => x.SignerName == "First Signer");
        var b = all.Single(x => x.SignerName == "Second Signer");
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, a.Status);
        Assert.Equal(SignatureStatus.Valid, b.Status);
        Assert.NotEqual(a.FieldName, b.FieldName);
        Assert.Empty(Signatures.BrokenByEdits(Signatures.Verify(once), twice));
    }

    [Fact]
    public void Certification_is_first_only_and_no_changes_blocks_signing()
    {
        using var cert = RsaCert();
        var certified = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Certify = CertificationLevel.FormFilling });
        Assert.Equal(SignatureStatus.Valid, Only(certified).Status);
        Assert.Equal(CertificationLevel.FormFilling, Signing.Certification(certified));
        Assert.Throws<SigningException>(() => Signing.Sign(certified, null, new SignRequest { Certificate = cert, Certify = CertificationLevel.FormFilling }));
        // Form filling and signing allowed, but only in the certifier's own signature fields: a new one is refused
        // (SignatureChangeTests covers signing an existing field).
        Assert.Throws<SigningException>(() => Signing.Sign(certified, null, new SignRequest { Certificate = cert }));
        var commentsToo = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Certify = CertificationLevel.FormFillingAndComments });
        var approved = Signing.Sign(commentsToo, null, new SignRequest { Certificate = cert });
        Assert.Equal(2, Signatures.Verify(approved).Count);
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Signatures.Verify(approved).Single(s => s.CertificationLevel == 3).Status);

        var locked = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Certify = CertificationLevel.NoChanges });
        Assert.Equal(CertificationLevel.NoChanges, Signing.Certification(locked));
        Assert.Throws<SigningException>(() => Signing.Sign(locked, null, new SignRequest { Certificate = cert }));
    }

    [Fact]
    public async Task Timestamp_token_is_embedded()
    {
        using var cert = RsaCert();
        using var tsa = new FakeTsa();
        var signed = await Signing.SignAsync(TestPdf.Make(1), null, new SignRequest { Certificate = cert, Timestamp = tsa.Respond });
        var s = Only(signed);
        Assert.Equal(SignatureStatus.Valid, s.Status);
        Assert.True(s.HasTimestamp);
        // The fake authority isn't trusted: its time doesn't vouch for the certificate, and the details say so.
        Assert.Contains("timestamp authority isn't trusted", s.Detail);
        Assert.Equal(1, tsa.Requests);
        Assert.InRange(s.SigningTime!.Value, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));

        // A broken server reply is a clear error.
        await Assert.ThrowsAsync<SigningException>(() => Signing.SignAsync(TestPdf.Make(1), null,
            new SignRequest { Certificate = cert, Timestamp = (_, _) => Task.FromResult(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x02 }) }));
    }

    [Fact]
    public void Pkcs7_format_and_existing_xref_stream_signed_file()
    {
        using var cert = RsaCert();
        var signed = Signing.Sign(IncrSignatureTests.SignedForm(xrefStream: true), null, new SignRequest { Certificate = cert, Format = SignatureFormat.Pkcs7 });
        var all = Signatures.Verify(signed);
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, all.Single(x => x.FieldName == "Approval").Status);
        var mine = all.Single(x => x.FieldName != "Approval");
        Assert.Equal(SignatureStatus.Valid, mine.Status);
        Assert.Equal("adbe.pkcs7.detached", mine.SubFilter);
    }

    [Fact]
    public void Encrypted_documents_are_refused()
    {
        using var cert = RsaCert();
        var ex = Assert.Throws<SigningException>(() => Signing.Sign(TestPdf.Encrypted(1, "pw"), "pw", new SignRequest { Certificate = cert }));
        Assert.Contains("Encrypted", ex.Message);
    }

    [Fact]
    public void Personal_id_is_rsa3072_document_signing_and_signs()
    {
        var pfx = DigitalIds.CreatePersonalPfx("Grace Hopper", "grace@example.com", "Navy", "secret");
        Assert.Throws<SigningException>(() => DigitalIds.LoadPfx(pfx, "wrong"));
        using var id = DigitalIds.LoadPfx(pfx, "secret");
        Assert.True(DigitalIds.IsUsable(id));
        Assert.Equal(3072, id.GetRSAPublicKey()!.KeySize);
        Assert.Contains(id.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == DigitalIds.DocumentSigningEku);
        Assert.InRange(id.NotAfter, DateTime.Now.AddYears(5).AddDays(-2), DateTime.Now.AddYears(5).AddDays(1));
        Assert.Contains("self-signed", DigitalIds.Describe(id));
        var signed = Signing.Sign(TestPdf.Make(1), null, new SignRequest { Certificate = id });
        Assert.Equal("Grace Hopper", Only(signed).SignerName);
    }

    [Fact]
    public void Handwritten_signature_stamp_put_move_remove()
    {
        var pdf = TestPdf.Make(1);
        var rect = new RectD(100, 400, 150, 50);
        var withSig = SignatureStamps.Put(pdf, null, 0, rect, Squiggle());
        var stamp = Assert.Single(SignatureStamps.Read(withSig, null));
        Assert.Equal(StampKind.Signature, stamp.Kind);
        Assert.Empty(TextStamps.Read(withSig, null));
        Assert.True(Core2Pdf.Ink(withSig, 0, rect) > 0.01);

        var moved = SignatureStamps.Put(withSig, null, 0, new RectD(300, 600, 75, 25), null, stamp.Id);
        var after = Assert.Single(SignatureStamps.Read(moved, null));
        Assert.Equal(stamp.Id, after.Id);
        Assert.InRange(after.Rect.X, 299.5, 300.5);
        Assert.True(Core2Pdf.Ink(moved, 0, new RectD(300, 600, 75, 25)) > 0.01);
        Assert.True(Core2Pdf.Ink(moved, 0, rect) < 0.001);

        var removed = TextStamps.Remove(moved, null, stamp.Id);
        Assert.Empty(SignatureStamps.Read(removed, null));
    }

    private static SignedCms Cms(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var at = text.LastIndexOf("/Contents <", StringComparison.Ordinal) + "/Contents <".Length;
        var end = text.IndexOf('>', at);
        // The reserved space is zero-padded after the DER: decode just the declared length.
        var reader = new AsnReader(Convert.FromHexString(text[at..end]), AsnEncodingRules.BER);
        var cms = new SignedCms();
        cms.Decode(reader.ReadEncodedValue().ToArray());
        return cms;
    }
}

/// <summary>An in-process RFC 3161 timestamp authority for tests.</summary>
internal sealed class FakeTsa : IDisposable
{
    private readonly X509Certificate2 _cert;
    private int _serial = 1;
    public int Requests { get; private set; }

    public FakeTsa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Fake TSA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], true));
        _cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public Task<byte[]> Respond(byte[] requestDer, CancellationToken cancel)
    {
        Requests++;
        Assert.True(Rfc3161TimestampRequest.TryDecode(requestDer, out var request, out _));
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteInteger(1);
            w.WriteObjectIdentifier("1.2.3.4.5");
            using (w.PushSequence())
            {
                using (w.PushSequence()) w.WriteObjectIdentifier(request!.HashAlgorithmId.Value!);
                w.WriteOctetString(request.GetMessageHash().Span);
            }
            w.WriteInteger(_serial++);
            w.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);
            if (request.GetNonce() is { } nonce) w.WriteInteger(nonce.Span);
        }
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), w.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _cert) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly };
        signer.SignedAttributes.Add(new AsnEncodedData(Signing.SigningCertificateV2Oid, Signing.SigningCertificateV2(_cert, HashAlgorithmName.SHA256)));
        cms.ComputeSignature(signer);
        var r = new AsnWriter(AsnEncodingRules.DER);
        using (r.PushSequence())
        {
            using (r.PushSequence()) r.WriteInteger(0);
            r.WriteEncodedValue(cms.Encode());
        }
        return Task.FromResult(r.Encode());
    }

    public void Dispose() => _cert.Dispose();
}
