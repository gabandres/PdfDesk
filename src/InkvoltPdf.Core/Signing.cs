using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>The CMS flavour written into /SubFilter.</summary>
public enum SignatureFormat
{
    /// <summary>ETSI.CAdES.detached (PAdES baseline): signing-certificate-v2 signed attribute, signing time in /M only.</summary>
    CAdES,
    /// <summary>adbe.pkcs7.detached: the classic Acrobat format, with a signing-time signed attribute.</summary>
    Pkcs7,
}

/// <summary>Certification (DocMDP): which later changes keep a certified document's certification valid.</summary>
public enum CertificationLevel
{
    None = 0,
    NoChanges = 1,
    FormFilling = 2,
    FormFillingAndComments = 3,
}

/// <summary>What a visible signature shows: text on the left, an optional image (e.g. a handwritten signature) on the right.</summary>
public sealed record SignatureLook
{
    public bool ShowName { get; init; } = true;
    public bool ShowDate { get; init; } = true;
    public bool ShowReason { get; init; } = true;
    public bool ShowLocation { get; init; }
    public bool ShowDistinguishedName { get; init; }
    public bool ShowLabels { get; init; } = true;
    /// <summary>Name shown instead of the certificate's common name.</summary>
    public string? Name { get; init; }
    public StampImage? Image { get; init; }
}

/// <summary>One digital signature to apply.</summary>
public sealed class SignRequest
{
    /// <summary>The signing certificate with its private key (software, smart card or Windows Hello: the OS asks for a PIN).</summary>
    public required X509Certificate2 Certificate { get; init; }
    /// <summary>An existing empty signature field to sign; null → a new field.</summary>
    public string? FieldName { get; init; }
    /// <summary>Page of a new visible signature.</summary>
    public int PageIndex { get; init; }
    /// <summary>Rect (page space) of a new visible signature; null → invisible.</summary>
    public RectD? Rect { get; init; }
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string? ContactInfo { get; init; }
    public SignatureLook Look { get; init; } = new();
    public SignatureFormat Format { get; init; } = SignatureFormat.CAdES;
    /// <summary>
    /// Sends a DER RFC 3161 TimeStampReq to a timestamp server and returns its DER TimeStampResp (the request holds only a
    /// hash of the signature). Null → no timestamp.
    /// </summary>
    public Func<byte[], CancellationToken, Task<byte[]>>? Timestamp { get; init; }
    public CertificationLevel Certify { get; init; }
    /// <summary>The time written to /M and shown in the appearance (default: now).</summary>
    public DateTimeOffset? Time { get; init; }
}

/// <summary>A signing refusal the UI shows as is.</summary>
public sealed class SigningException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Applies digital signatures (PAdES / PKCS#7 detached CMS) to PDFs. The signature is always appended as an incremental
/// update (see <see cref="Incremental"/>), so the original bytes, and any earlier signature, stay intact; /Contents is
/// reserved as a zero-filled hex string, the /ByteRange around it is patched in and hashed, and the CMS is written into
/// the gap. A signature field's /Lock is honored (FieldMDP reference, locked fields made read-only). Not supported:
/// encrypted documents, LTV (DSS/VRI, embedded revocation data).
/// </summary>
public static class Signing
{
    internal const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";
    internal const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string RangePlaceholder = "[0 9999999999 9999999999 9999999999]";

    /// <summary>Signs on the calling thread (blocks on the timestamp delegate: don't call from the UI thread with one).</summary>
    public static byte[] Sign(byte[] data, string? password, SignRequest req) =>
        SignAsync(data, password, req).GetAwaiter().GetResult();

    public static async Task<byte[]> SignAsync(byte[] data, string? password, SignRequest req, CancellationToken cancel = default)
    {
        if (data.AsSpan().IndexOf("/Encrypt"u8) >= 0)
            throw new SigningException("Encrypted (password-protected) PDFs can't be signed here. Remove the protection first (Save As without a password), then sign.");
        var cert = req.Certificate;
        if (!cert.HasPrivateKey) throw new SigningException("This digital ID has no private key, so it can't sign.");
        var hash = DigestFor(cert);

        var existing = Signatures.Verify(data, password);
        var signedBefore = existing.Any(s => s.Status != SignatureStatus.Unsigned);
        if (req.Certify != CertificationLevel.None && signedBefore)
            throw new SigningException("A certifying signature must be the document's first signature: this document is already signed.");
        var certification = Certification(data, password);
        if (certification == CertificationLevel.NoChanges)
            throw new SigningException("This document is certified with no changes allowed: another signature would invalidate the certification.");
        // Form filling and signing (P=2) covers signing the signature fields the certifier put there, not adding new ones.
        if (certification == CertificationLevel.FormFilling && req.FieldName is null)
            throw new SigningException("This document is certified to allow only filling in forms and signing its existing signature fields: sign one of its empty signature fields instead.");
        if (existing.FirstOrDefault(s => s.Status == SignatureStatus.DisallowedChanges && s.CertificationLevel is not null) is { } broken)
            throw new SigningException($"The changes made to this document since it was certified aren't allowed by the certification ({broken.Detail}). Undo them, then sign.");

        var reserve = req.Timestamp is null ? 16384 : 32768;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            var (pdf, gapStart, gapEnd) = Prepare(data, password, req, cert, reserve, signedBefore);
            var signed = new byte[gapStart + (pdf.Length - gapEnd)];
            Array.Copy(pdf, 0, signed, 0, gapStart);
            Array.Copy(pdf, gapEnd, signed, gapStart, pdf.Length - gapEnd);
            var der = await BuildCms(signed, cert, hash, req, cancel).ConfigureAwait(false);
            var hex = Convert.ToHexString(der);
            if (hex.Length <= gapEnd - gapStart - 2)
            {
                Encoding.ASCII.GetBytes(hex).CopyTo(pdf, gapStart + 1);
                return pdf;
            }
            reserve = der.Length + 8192;
        }
        throw new SigningException("The signature doesn't fit the space reserved for it.");
    }

    /// <summary>The certification level of a document's certifying signature (catalog /Perms /DocMDP), else None.</summary>
    public static CertificationLevel Certification(byte[] data, string? password = null)
    {
        try
        {
            using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
            if (PdfObj.Dict(PdfObj.Dict(doc.Internals.Catalog.Elements["/Perms"])?.Elements["/DocMDP"]) is not { } sig) return CertificationLevel.None;
            foreach (var r in PdfObj.Array(sig.Elements["/Reference"])?.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>() ?? [])
                if (PdfObj.NameOf(r, "/TransformMethod") == "/DocMDP")
                    return PdfObj.Int(PdfObj.Dict(r.Elements["/TransformParams"])?.Elements["/P"]) is { } p and >= 1 and <= 3 ? (CertificationLevel)p : CertificationLevel.FormFilling;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable: treat as not certified (signing will fail on its own if the file can't be edited).
        }
        return CertificationLevel.None;
    }

    // ---- The PDF side: field, appearance, signature dictionary with placeholders, incremental update ----

    private static (byte[] Pdf, int GapStart, int GapEnd) Prepare(byte[] data, string? password, SignRequest req, X509Certificate2 cert, int reserve, bool signedBefore)
    {
        var source = data;
        var doc = OpenTracked(source, password);
        if (doc is null)
        {
            // The file's structure can't be appended to (no usable last xref): normalize it first, which is only safe
            // when nothing is signed yet.
            if (signedBefore) throw new SigningException("This signed document's file structure can't be extended, so a new signature would break the existing ones.");
            using (var full = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be signed")) source = PdfObj.Save(full, rewrite: true);
            doc = OpenTracked(source, password) ?? throw new SigningException("This PDF's structure can't be signed incrementally.");
        }
        using (doc)
        {
            var time = req.Time ?? DateTimeOffset.Now;
            var catalog = doc.Internals.Catalog;
            var acro = PdfObj.Dict(catalog.Elements["/AcroForm"]);
            if (acro is null)
            {
                acro = new PdfDictionary(doc);
                catalog.Elements["/AcroForm"] = acro;
            }
            if (PdfObj.Array(acro.Elements["/Fields"]) is not { } fields)
            {
                fields = new PdfArray(doc);
                acro.Elements["/Fields"] = fields;
            }
            acro.Elements["/SigFlags"] = new PdfInteger(3);

            PdfDictionary widget;
            PdfDictionary fieldDict;
            PdfPage page;
            RectD rect;
            if (req.FieldName is { } name)
            {
                var all = new List<FormEditor.Field>();
                FormEditor.Collect(doc, acro, all);
                var f = all.FirstOrDefault(x => x.Name == name) ?? throw new SigningException($"There is no field named \"{name}\".");
                if (f.Type != "/Sig") throw new SigningException($"\"{name}\" isn't a signature field.");
                if (FormEditor.Inherited(f.Dict, "/V") is not null and not PdfNull) throw new SigningException($"\"{name}\" is already signed.");
                fieldDict = f.Dict;
                widget = f.Widgets.FirstOrDefault() ?? f.Dict;
                var pi = PdfObj.PageIndexOf(doc, widget.Elements["/P"]);
                if (pi < 0) pi = PageOfAnnot(doc, widget);
                page = doc.Pages[Math.Max(0, pi)];
                rect = PdfObj.Array(widget.Elements["/Rect"]) is { } r ? PdfObj.RectToPage(page, r) : default;
                if (pi < 0) PdfObj.Annots(doc, page).Elements.Add(widget.Reference ?? Indirect(doc, widget).Reference!);
            }
            else
            {
                if (req.PageIndex < 0 || req.PageIndex >= doc.PageCount) throw new SigningException("The signature's page doesn't exist.");
                page = doc.Pages[req.PageIndex];
                rect = req.Rect ?? default;
                widget = fieldDict = new PdfDictionary(doc);
                widget.Elements["/Type"] = new PdfName("/Annot");
                widget.Elements["/Subtype"] = new PdfName("/Widget");
                widget.Elements["/FT"] = new PdfName("/Sig");
                widget.Elements["/T"] = PdfObj.MakeString(NewFieldName(doc, acro));
                widget.Elements["/F"] = new PdfInteger(132); // print + locked
                widget.Elements["/P"] = page.Reference!;
                if (rect.IsEmpty)
                    widget.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
                else
                {
                    var (x1, y1, x2, y2) = TextStamps.FromPageSpace(page, rect);
                    widget.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
                }
                doc.Internals.AddObject(widget);
                PdfObj.Annots(doc, page).Elements.Add(widget.Reference!);
                fields.Elements.Add(widget.Reference!);
            }

            // Appearance (an empty one for invisible signatures).
            var normal = new PdfDictionary(doc);
            normal.Elements["/N"] = Appearance(doc, page, rect, cert, req, time).Reference!;
            widget.Elements["/AP"] = normal;

            var sig = new PdfDictionary(doc);
            sig.Elements["/Type"] = new PdfName("/Sig");
            sig.Elements["/Filter"] = new PdfName("/Adobe.PPKLite");
            sig.Elements["/SubFilter"] = new PdfName(req.Format == SignatureFormat.CAdES ? "/ETSI.CAdES.detached" : "/adbe.pkcs7.detached");
            sig.Elements["/ByteRange"] = new PdfLiteral(RangePlaceholder);
            sig.Elements["/Contents"] = new PdfLiteral($"<{new string('0', reserve * 2)}>");
            sig.Elements["/M"] = PdfObj.DateString(time);
            var signer = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrEmpty(signer)) sig.Elements["/Name"] = PdfObj.MakeString(signer);
            if (!string.IsNullOrWhiteSpace(req.Reason)) sig.Elements["/Reason"] = PdfObj.MakeString(req.Reason);
            if (!string.IsNullOrWhiteSpace(req.Location)) sig.Elements["/Location"] = PdfObj.MakeString(req.Location);
            if (!string.IsNullOrWhiteSpace(req.ContactInfo)) sig.Elements["/ContactInfo"] = PdfObj.MakeString(req.ContactInfo);
            var app = new PdfDictionary(doc);
            app.Elements["/Name"] = new PdfName("/InkvoltPdf");
            sig.Elements["/Prop_Build"] = new PdfDictionary(doc) { Elements = { ["/App"] = app } };
            if (req.Certify != CertificationLevel.None)
            {
                var tp = new PdfDictionary(doc);
                tp.Elements["/Type"] = new PdfName("/TransformParams");
                tp.Elements["/P"] = new PdfInteger((int)req.Certify);
                tp.Elements["/V"] = new PdfName("/1.2");
                var sigRef = new PdfDictionary(doc);
                sigRef.Elements["/Type"] = new PdfName("/SigRef");
                sigRef.Elements["/TransformMethod"] = new PdfName("/DocMDP");
                sigRef.Elements["/TransformParams"] = tp;
                sig.Elements["/Reference"] = new PdfArray(doc, sigRef);
            }
            // A signature field's /Lock names the fields signing it locks (FieldMDP): record it in the signature, and make
            // those fields read-only, as Acrobat does.
            if (req.FieldName is not null && PdfObj.Dict(fieldDict.Elements["/Lock"]) is { } lockDict)
            {
                var tp = new PdfDictionary(doc);
                tp.Elements["/Type"] = new PdfName("/TransformParams");
                tp.Elements["/Action"] = lockDict.Elements["/Action"] ?? new PdfName("/All");
                if (lockDict.Elements["/Fields"] is { } lockFields) tp.Elements["/Fields"] = lockFields;
                tp.Elements["/V"] = new PdfName("/1.2");
                var sigRef = new PdfDictionary(doc);
                sigRef.Elements["/Type"] = new PdfName("/SigRef");
                sigRef.Elements["/TransformMethod"] = new PdfName("/FieldMDP");
                sigRef.Elements["/TransformParams"] = tp;
                if (PdfObj.Array(sig.Elements["/Reference"]) is { } refs) refs.Elements.Add(sigRef);
                else sig.Elements["/Reference"] = new PdfArray(doc, sigRef);
                LockFields(doc, acro, lockDict, req.FieldName);
            }
            doc.Internals.AddObject(sig);
            fieldDict.Elements["/V"] = sig.Reference!;
            if (req.Certify != CertificationLevel.None)
                catalog.Elements["/Perms"] = new PdfDictionary(doc) { Elements = { ["/DocMDP"] = sig.Reference! } };

            var pdf = Incremental.TrySave(doc, Xmp.Read(doc)) ?? throw new SigningException("The signature can't be appended to this PDF.");
            var from = source.Length;
            var gapStart = LastIndexOf(pdf, Encoding.ASCII.GetBytes($"<{new string('0', reserve * 2)}>"), from);
            var rangeAt = LastIndexOf(pdf, Encoding.ASCII.GetBytes(RangePlaceholder), from);
            if (gapStart < 0 || rangeAt < 0) throw new SigningException("The signature placeholder wasn't written.");
            var gapEnd = gapStart + reserve * 2 + 2;
            var range = $"[0 {gapStart} {gapEnd} {pdf.Length - gapEnd}";
            range = range.PadRight(RangePlaceholder.Length - 1) + "]";
            Encoding.ASCII.GetBytes(range).CopyTo(pdf, rangeAt);
            return (pdf, gapStart, gapEnd);
        }
    }

    private static PdfDocument? OpenTracked(byte[] data, string? password)
    {
        PdfDocument doc;
        try
        {
            var stream = new MemoryStream(data, writable: false);
            doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(stream, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(stream, password, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex)
        {
            throw PdfObj.ReaderError(ex, PdfDocumentOpenMode.Modify, "This PDF can't be signed");
        }
        Incremental.Track(doc, data, password, force: true);
        if (Incremental.IsTracked(doc)) return doc;
        doc.Dispose();
        return null;
    }

    private static PdfDictionary Indirect(PdfDocument doc, PdfDictionary d)
    {
        if (!d.IsIndirect) doc.Internals.AddObject(d);
        return d;
    }

    private static int PageOfAnnot(PdfDocument doc, PdfDictionary widget)
    {
        for (var p = 0; p < doc.PageCount; p++)
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is { } annots && annots.Elements.Any(e => ReferenceEquals(PdfObj.Dict(e), widget)))
                return p;
        return -1;
    }

    /// <summary>Sets the read-only flag on the fields a signature field's /Lock names (/All, /Include or /Exclude).</summary>
    private static void LockFields(PdfDocument doc, PdfDictionary acro, PdfDictionary lockDict, string signatureField)
    {
        var names = PdfObj.Array(lockDict.Elements["/Fields"])?.Elements.Select(PdfObj.Text).OfType<string>().ToHashSet() ?? [];
        bool Listed(string name) => names.Any(n => name == n || name.StartsWith(n + ".", StringComparison.Ordinal));
        var action = PdfObj.NameOf(lockDict, "/Action");
        var all = new List<FormEditor.Field>();
        FormEditor.Collect(doc, acro, all);
        foreach (var f in all)
        {
            if (f.Name == signatureField) continue;
            var locked = action switch { "/Include" => Listed(f.Name), "/Exclude" => !Listed(f.Name), _ => true };
            if (locked) f.Dict.Elements["/Ff"] = new PdfInteger((PdfObj.Int(f.Dict.Elements["/Ff"]) ?? 0) | 1);
        }
    }

    private static string NewFieldName(PdfDocument doc, PdfDictionary acro)
    {
        var all = new List<FormEditor.Field>();
        FormEditor.Collect(doc, acro, all);
        var names = all.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
            if (!names.Contains($"Signature{n}")) return $"Signature{n}";
    }

    private static int LastIndexOf(byte[] data, byte[] needle, int from)
    {
        var i = data.AsSpan(from).LastIndexOf(needle);
        return i < 0 ? -1 : from + i;
    }

    /// <summary>The visible signature's look: details on the left, the image on the right; upright on rotated pages.</summary>
    private static PdfDictionary Appearance(PdfDocument doc, PdfPage page, RectD rect, X509Certificate2 cert, SignRequest req, DateTimeOffset time)
    {
        if (rect.IsEmpty) return PdfObj.FormXObject(doc, [], 0, 0, 0, 0, null);
        var look = req.Look;
        double w = rect.Width, h = rect.Height;
        var name = look.Name ?? cert.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        var lines = new List<string>();
        string L(string label, string value) => look.ShowLabels ? $"{label}: {value}" : value;
        if (look.ShowName) lines.Add(look.ShowLabels ? $"Digitally signed by {name}" : name);
        if (look.ShowDistinguishedName) lines.Add(L("DN", cert.Subject));
        if (look.ShowReason && !string.IsNullOrWhiteSpace(req.Reason)) lines.Add(L("Reason", req.Reason));
        if (look.ShowLocation && !string.IsNullOrWhiteSpace(req.Location)) lines.Add(L("Location", req.Location));
        if (look.ShowDate) lines.Add(L("Date", time.ToString("yyyy.MM.dd HH:mm:ss zzz", CultureInfo.InvariantCulture)));

        var content = new StringBuilder();
        var pad = Math.Min(4, Math.Min(w, h) * 0.05);
        var textW = w - 2 * pad;
        PdfDictionary? image = null;
        if (look.Image is { Width: > 0, Height: > 0 } img)
        {
            image = SignatureStamps.ImageXObject(doc, img);
            var aspect = (double)img.Width / img.Height;
            var boxW = lines.Count == 0 ? w - 2 * pad : Math.Min((w - 2 * pad) * 0.5, (h - 2 * pad) * aspect);
            var iw = Math.Min(boxW, (h - 2 * pad) * aspect);
            var ih = iw / aspect;
            var ix = w - pad - boxW + (boxW - iw) / 2;
            var iy = (h - ih) / 2;
            content.Append(CultureInfo.InvariantCulture, $"q {PdfObj.F(iw)} 0 0 {PdfObj.F(ih)} {PdfObj.F(ix)} {PdfObj.F(iy)} cm /Im1 Do Q\n");
            textW = w - 3 * pad - boxW;
        }

        PdfDictionary resources;
        if (lines.Count > 0 && textW > 2)
        {
            var painter = new TextPainter(StdFont.Helvetica, lines);
            var widest = lines.Max(painter.Measure);
            var size = Math.Min((h - 2 * pad) / (lines.Count * 1.2), widest > 0 ? textW / widest : 12);
            size = Math.Clamp(size, 1, 14);
            var y = h - pad - size;
            var block = lines.Count * size * 1.2;
            y -= Math.Max(0, (h - 2 * pad - block) / 2);
            foreach (var line in lines)
            {
                painter.Draw(line, pad, y, size);
                y -= size * 1.2;
            }
            var (ops, res) = painter.Build(doc, w, h, "/Helv", PdfColorValue.Black);
            content.Append(Encoding.Latin1.GetString(ops));
            resources = res;
        }
        else resources = new PdfDictionary(doc);
        if (image is not null) resources.Elements["/XObject"] = new PdfDictionary(doc) { Elements = { ["/Im1"] = image.Reference! } };
        return PdfObj.FormXObject(doc, Encoding.Latin1.GetBytes(content.ToString()), 0, 0, w, h, resources, PdfObj.CounterRotation(doc, page));
    }

    // ---- The CMS side ----

    /// <summary>SHA-256, or SHA-384/512 for EC keys on P-384/P-521 (matching the curve's strength).</summary>
    internal static HashAlgorithmName DigestFor(X509Certificate2 cert)
    {
        using var ec = cert.GetECDsaPublicKey();
        return ec?.KeySize switch
        {
            384 => HashAlgorithmName.SHA384,
            521 => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };
    }

    private static Oid OidOf(HashAlgorithmName h) => new(h == HashAlgorithmName.SHA384 ? "2.16.840.1.101.3.4.2.2"
        : h == HashAlgorithmName.SHA512 ? "2.16.840.1.101.3.4.2.3" : "2.16.840.1.101.3.4.2.1");

    private static async Task<byte[]> BuildCms(byte[] signed, X509Certificate2 cert, HashAlgorithmName hash, SignRequest req, CancellationToken cancel)
    {
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        CmsSigner Signer(X509IncludeOption include)
        {
            var s = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert) { DigestAlgorithm = OidOf(hash), IncludeOption = include };
            s.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, SigningCertificateV2(cert, hash)));
            if (req.Format == SignatureFormat.Pkcs7) s.SignedAttributes.Add(new Pkcs9SigningTime((req.Time ?? DateTimeOffset.Now).UtcDateTime));
            return s;
        }
        try
        {
            cms.ComputeSignature(Signer(X509IncludeOption.WholeChain), silent: false);
        }
        catch (CryptographicException ex) when (IsCancellation(ex))
        {
            throw new OperationCanceledException("Signing was cancelled.", ex);
        }
        catch (CryptographicException)
        {
            // The chain can't be gathered (e.g. a missing intermediate): sign with the end certificate alone.
            try
            {
                cms = new SignedCms(new ContentInfo(signed), detached: true);
                cms.ComputeSignature(Signer(X509IncludeOption.EndCertOnly), silent: false);
            }
            catch (CryptographicException ex) when (IsCancellation(ex))
            {
                throw new OperationCanceledException("Signing was cancelled.", ex);
            }
            catch (CryptographicException ex)
            {
                throw new SigningException($"The digital ID couldn't sign: {ex.Message}", ex);
            }
        }

        if (req.Timestamp is { } tsa)
        {
            var nonce = RandomNumberGenerator.GetBytes(8);
            nonce[0] = (byte)((nonce[0] & 0x7F) | 0x01);
            var tsReq = Rfc3161TimestampRequest.CreateFromSignerInfo(cms.SignerInfos[0], HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: nonce);
            byte[] reply;
            try
            {
                reply = await tsa(tsReq.Encode(), cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new SigningException($"The timestamp server couldn't be reached: {ex.Message}", ex);
            }
            Rfc3161TimestampToken token;
            try
            {
                token = tsReq.ProcessResponse(reply, out _);
            }
            catch (CryptographicException ex)
            {
                throw new SigningException($"The timestamp server's reply isn't usable: {ex.Message}", ex);
            }
            cms.SignerInfos[0].AddUnsignedAttribute(new AsnEncodedData(TimestampTokenOid, token.AsSignedCms().Encode()));
        }
        return cms.Encode();
    }

    /// <summary>A PIN prompt (smart card, Windows Hello) the user cancelled.</summary>
    internal static bool IsCancellation(CryptographicException ex) =>
        (ex.HResult is unchecked((int)0x800704C7) or unchecked((int)0x8010006E) or unchecked((int)0x80090036)
                           || ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase));

    /// <summary>The ESS signing-certificate-v2 attribute value (RFC 5035): the signer certificate's hash and issuer/serial.</summary>
    internal static byte[] SigningCertificateV2(X509Certificate2 cert, HashAlgorithmName hash)
    {
        var digest = hash == HashAlgorithmName.SHA384 ? SHA384.HashData(cert.RawData)
            : hash == HashAlgorithmName.SHA512 ? SHA512.HashData(cert.RawData) : SHA256.HashData(cert.RawData);
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
        {
            if (hash != HashAlgorithmName.SHA256)
                using (w.PushSequence())
                    w.WriteObjectIdentifier(OidOf(hash).Value!);
            w.WriteOctetString(digest);
            using (w.PushSequence())
            {
                using (w.PushSequence())
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
                    w.WriteEncodedValue(cert.IssuerName.RawData);
                w.WriteInteger(Convert.FromHexString(cert.SerialNumber)); // big-endian, as encoded in the certificate
            }
        }
        return w.Encode();
    }
}
