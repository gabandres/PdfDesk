using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

public enum SignatureStatus
{
    /// <summary>The signature matches the signed bytes and covers the whole file.</summary>
    Valid,
    /// <summary>The signed bytes were altered, or the signature itself is broken.</summary>
    Invalid,
    /// <summary>The signature is intact but the file was changed after signing (later incremental updates).</summary>
    ModifiedAfterSigning,
    /// <summary>A signature format not verified here (e.g. adbe.x509.rsa_sha1).</summary>
    Unsupported,
    /// <summary>An empty signature field.</summary>
    Unsigned,
}

/// <summary>One signature field and the result of checking it.</summary>
/// <param name="PageIndex">Page of the field's widget, or -1 for an invisible signature.</param>
/// <param name="Rect">Widget rect in page space (empty for invisible signatures).</param>
/// <param name="SignerName">Common name of the signing certificate (else the /Name entry).</param>
/// <param name="SigningTime">The timestamp token's time, else the signed signing-time attribute, else /M.</param>
/// <param name="HasTimestamp">Carries an RFC 3161 timestamp (or is a document timestamp).</param>
/// <param name="CertificateTrusted">The certificate chain builds to a root trusted by Windows (revocation not checked unless asked).</param>
/// <param name="CertificateStatus">"OK" or the chain problems (e.g. "UntrustedRoot, NotTimeValid").</param>
/// <param name="CertificateValidAtSigningTime">The certificate's validity period includes the signing time (null when unknown).</param>
/// <param name="Detail">Why a signature is invalid or unsupported.</param>
public sealed record SignatureInfo(
    string FieldName,
    int PageIndex,
    RectD Rect,
    SignatureStatus Status,
    string? SignerName,
    DateTimeOffset? SigningTime,
    string? Reason,
    string? Location,
    string? SubFilter,
    bool CoversWholeDocument,
    bool HasTimestamp,
    bool CertificateTrusted,
    string CertificateStatus,
    bool? CertificateValidAtSigningTime,
    string? Detail,
    X509Certificate2? Certificate);

/// <summary>
/// Verifies digital signatures on the raw file bytes: /ByteRange digests and CMS signatures
/// (adbe.pkcs7.detached, ETSI.CAdES.detached, adbe.pkcs7.sha1, ETSI.RFC3161 document timestamps).
/// Certificate trust uses the Windows certificate store; revocation is only checked when asked (it goes online).
/// </summary>
public static class Signatures
{
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>
    /// The signatures that checked out in <paramref name="original"/> (the <see cref="Verify"/> result kept from when the
    /// file was opened) but no longer do in <paramref name="current"/> (the edited bytes). Edits to a signed file are
    /// appended as incremental updates, which keeps its signatures intact (<see cref="SignatureStatus.ModifiedAfterSigning"/>);
    /// a redaction or an encrypted file is rewritten, which breaks them: the UI can say "changed by your edits" for these
    /// instead of presenting them as forged. Matched by field name.
    /// </summary>
    public static IReadOnlyList<SignatureInfo> BrokenByEdits(IReadOnlyList<SignatureInfo> original, byte[] current, string? password = null)
    {
        var intact = original.Where(s => s.Status is SignatureStatus.Valid or SignatureStatus.ModifiedAfterSigning).ToList();
        if (intact.Count == 0) return [];
        var now = Verify(current, password);
        return [.. intact.Where(s => now.FirstOrDefault(n => n.FieldName == s.FieldName)?.Status is not (SignatureStatus.Valid or SignatureStatus.ModifiedAfterSigning))];
    }

    public static IReadOnlyList<SignatureInfo> Verify(byte[] data, string? password = null, bool checkRevocationOnline = false)
    {
        var results = new List<SignatureInfo>();
        PdfDocument? doc = null;
        try
        {
            doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any parser failure on a hostile or damaged file: fall back to the raw scan, never throw.
            doc = null;
        }

        if (doc is null) return RawScan(data, checkRevocationOnline);
        using (doc)
        {
            var fields = new List<FormEditor.Field>();
            var pageOf = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
            try
            {
                FormEditor.Collect(doc, PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]), fields);
                for (var p = 0; p < doc.PageCount; p++)
                    if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is { } annots)
                        foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>()) pageOf.TryAdd(a, p);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return RawScan(data, checkRevocationOnline);
            }

            foreach (var f in fields.Where(f => f.Type == "/Sig"))
            {
                var page = -1;
                var rect = default(RectD);
                try
                {
                    foreach (var w in f.Widgets)
                    {
                        if (!pageOf.TryGetValue(w, out var p) || PdfObj.Array(w.Elements["/Rect"]) is not { } r) continue;
                        page = p;
                        rect = PdfObj.RectToPage(doc.Pages[p], r);
                        break;
                    }
                    var v = PdfObj.Dict(FormEditor.Inherited(f.Dict, "/V"));
                    if (v is null || PdfObj.Array(v.Elements["/ByteRange"]) is not { Elements.Count: >= 4 } br)
                    {
                        results.Add(new SignatureInfo(f.Name, page, rect, SignatureStatus.Unsigned, null, null, null, null, null, false, false, false, "", null, null, null));
                        continue;
                    }
                    var range = new long[4];
                    for (var i = 0; i < 4; i++)
                    {
                        var n = PdfObj.Num(br, i);
                        range[i] = double.IsFinite(n) && n >= 0 && n <= data.Length && n == Math.Floor(n) ? (long)n : -1;
                    }
                    results.Add(Check(data, range, PdfObj.Name(v.Elements["/SubFilter"]), f.Name, page, rect,
                        PdfObj.Str(v.Elements["/Name"]), PdfObj.ParseDate(PdfObj.Str(v.Elements["/M"])),
                        PdfObj.Str(v.Elements["/Reason"]), PdfObj.Str(v.Elements["/Location"]), checkRevocationOnline));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    results.Add(new SignatureInfo(f.Name, page, rect, SignatureStatus.Invalid, null, null, null, null, null, false, false, false, "", null,
                        $"The signature can't be read: {ex.Message}", null));
                }
            }
        }
        return results;
    }

    /// <summary>Fallback when the file can't be parsed: find /ByteRange entries in the raw bytes.</summary>
    private static List<SignatureInfo> RawScan(byte[] data, bool online)
    {
        var text = Encoding.Latin1.GetString(data);
        var list = new List<SignatureInfo>();
        var seen = new HashSet<string>();
        var n = 0;
        foreach (Match m in Regex.Matches(text, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]"))
        {
            if (!seen.Add(m.Value)) continue;
            var range = Enumerable.Range(1, 4).Select(i => long.TryParse(m.Groups[i].Value, PdfObj.Inv, out var v) && v <= data.Length ? v : -1).ToArray();
            var window = text.Substring(Math.Max(0, m.Index - 4000), Math.Min(8000, text.Length - Math.Max(0, m.Index - 4000)));
            var sub = Regex.Match(window, @"/SubFilter\s*/([\w.]+)");
            var name = $"Signature{++n}";
            try
            {
                list.Add(Check(data, range, sub.Success ? sub.Groups[1].Value : null, name, -1, default, null, null, null, null, online));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                list.Add(new SignatureInfo(name, -1, default, SignatureStatus.Invalid, null, null, null, null, null, false, false, false, "", null,
                    $"The signature can't be read: {ex.Message}", null));
            }
        }
        return list;
    }

    private static SignatureInfo Check(byte[] data, long[] br, string? subFilter, string field, int page, RectD rect,
        string? nameEntry, DateTimeOffset? mDate, string? reason, string? location, bool online)
    {
        SignatureInfo Result(SignatureStatus status, string? detail, bool covers = false) =>
            new(field, page, rect, status, nameEntry, mDate, reason, location, subFilter, covers, false, false, "", null, detail, null);

        // Each entry is checked against the file length first, so the sums below can't overflow.
        if (br.Any(x => x < 0 || x > data.Length) || br[2] < br[0] + br[1] || br[2] + br[3] > data.Length)
            return Result(SignatureStatus.Invalid, "The signature's byte range doesn't fit the file.");
        var covers = br[0] == 0 && data.AsSpan((int)(br[2] + br[3])).ToArray().All(b => b is (byte)' ' or (byte)'\r' or (byte)'\n' or 0 or (byte)'\t');

        var signed = new byte[br[1] + br[3]];
        Array.Copy(data, br[0], signed, 0, br[1]);
        Array.Copy(data, br[2], signed, br[1], br[3]);
        var gap = data.AsSpan((int)(br[0] + br[1]), (int)(br[2] - br[0] - br[1]));
        // The unsigned gap must hold nothing but the hex signature value: anything else there (objects slipped in where
        // the zero padding was) would be unsigned content shown as signed.
        if (!IsHexStringOnly(gap)) return Result(SignatureStatus.Invalid, "The signature's unsigned gap holds more than the signature value.");
        var contents = HexContents(gap);
        if (contents is null || contents.Length == 0) return Result(SignatureStatus.Invalid, "The signature value is missing.");

        X509Certificate2? cert = null;
        X509Certificate2Collection extra = [];
        DateTimeOffset? time = null;
        var hasTimestamp = false;
        string? detail = null;
        bool ok;
        try
        {
            switch (subFilter)
            {
                case "adbe.pkcs7.detached" or "ETSI.CAdES.detached":
                {
                    var cms = new SignedCms(new ContentInfo(signed), detached: true);
                    cms.Decode(contents);
                    (cert, extra, time, hasTimestamp) = SignerOf(cms);
                    ok = TryCheck(cms, ref detail);
                    break;
                }
                case "adbe.pkcs7.sha1":
                {
                    var cms = new SignedCms();
                    cms.Decode(contents);
                    (cert, extra, time, hasTimestamp) = SignerOf(cms);
                    ok = TryCheck(cms, ref detail);
                    var content = cms.ContentInfo.Content;
                    if (content.Length == 22 && content[0] == 0x04 && content[1] == 20) content = content[2..];
                    if (ok && !content.AsSpan().SequenceEqual(SHA1.HashData(signed)))
                    {
                        ok = false;
                        detail = "The document digest doesn't match.";
                    }
                    break;
                }
                case "ETSI.RFC3161":
                {
                    if (!Rfc3161TimestampToken.TryDecode(contents, out var token, out _))
                        return Result(SignatureStatus.Invalid, "The timestamp token can't be read.", covers);
                    hasTimestamp = true;
                    time = token.TokenInfo.Timestamp;
                    ok = token.VerifySignatureForData(signed, out cert, null);
                    if (!ok) detail = "The timestamp doesn't match the document.";
                    break;
                }
                default:
                    return Result(SignatureStatus.Unsupported, $"Signature format {subFilter ?? "(none)"} isn't supported.", covers);
            }
        }
        catch (CryptographicException ex)
        {
            return Result(SignatureStatus.Invalid, $"The signature can't be read: {ex.Message}", covers);
        }

        time ??= mDate;
        var (trusted, chainStatus) = cert is null ? (false, "No certificate") : Trust(cert, extra, time, online);
        bool? validAtTime = cert is null || time is null ? null : cert.NotBefore <= time.Value.LocalDateTime && time.Value.LocalDateTime <= cert.NotAfter;
        var status = !ok ? SignatureStatus.Invalid : covers ? SignatureStatus.Valid : SignatureStatus.ModifiedAfterSigning;
        if (status == SignatureStatus.ModifiedAfterSigning) detail ??= "The document was changed after it was signed.";
        var signer = cert?.GetNameInfo(X509NameType.SimpleName, false);
        return new SignatureInfo(field, page, rect, status, string.IsNullOrEmpty(signer) ? nameEntry : signer, time, reason, location, subFilter,
            covers, hasTimestamp, trusted, chainStatus, validAtTime, detail, cert);
    }

    private static bool TryCheck(SignedCms cms, ref string? detail)
    {
        if (cms.SignerInfos.Count == 0)
        {
            detail = "The signature has no signer.";
            return false;
        }
        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
            return true;
        }
        catch (CryptographicException ex)
        {
            detail = $"The signature doesn't match the document: {ex.Message}";
            return false;
        }
    }

    private static (X509Certificate2? Cert, X509Certificate2Collection Extra, DateTimeOffset? Time, bool HasTimestamp) SignerOf(SignedCms cms)
    {
        if (cms.SignerInfos.Count == 0) return (null, cms.Certificates, null, false);
        var si = cms.SignerInfos[0];
        DateTimeOffset? time = null;
        var hasTs = false;
        foreach (var attr in si.UnsignedAttributes)
            if (attr.Oid.Value == TimestampTokenOid && attr.Values.Count > 0 && Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var tok, out _))
            {
                time = tok.TokenInfo.Timestamp;
                hasTs = true;
            }
        if (time is null)
            foreach (var attr in si.SignedAttributes)
                foreach (var v in attr.Values)
                    if (v is Pkcs9SigningTime st) time = new DateTimeOffset(DateTime.SpecifyKind(st.SigningTime, DateTimeKind.Utc));
        return (si.Certificate, cms.Certificates, time, hasTs);
    }

    private static (bool Trusted, string Status) Trust(X509Certificate2 cert, X509Certificate2Collection extra, DateTimeOffset? time, bool online)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = online ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
            chain.ChainPolicy.ExtraStore.AddRange(extra);
            if (time is { } t) chain.ChainPolicy.VerificationTime = t.LocalDateTime;
            var trusted = chain.Build(cert);
            var problems = chain.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError).Select(s => s.Status.ToString()).Distinct().ToList();
            return (trusted, trusted || problems.Count == 0 ? "OK" : string.Join(", ", problems));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return (false, $"Chain can't be built: {ex.Message}");
        }
    }

    /// <summary>True when the bytes are exactly one hex string: '&lt;', hex digits (whitespace allowed), '&gt;'.</summary>
    private static bool IsHexStringOnly(ReadOnlySpan<byte> gap)
    {
        if (gap.Length < 2 || gap[0] != (byte)'<' || gap[^1] != (byte)'>') return false;
        foreach (var b in gap[1..^1])
            if (!Uri.IsHexDigit((char)b) && b is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')) return false;
        return true;
    }

    /// <summary>The DER bytes of a /Contents hex string (zero padding after the CMS structure trimmed).</summary>
    private static byte[]? HexContents(ReadOnlySpan<byte> gap)
    {
        var open = gap.IndexOf((byte)'<');
        var close = gap.LastIndexOf((byte)'>');
        if (open < 0 || close <= open) return null;
        var hex = new StringBuilder();
        foreach (var b in gap[(open + 1)..close])
            if (Uri.IsHexDigit((char)b)) hex.Append((char)b);
        if (hex.Length % 2 == 1) hex.Append('0');
        var bytes = Convert.FromHexString(hex.ToString());
        // DER: 0x30, length → the real size.
        if (bytes.Length > 2 && bytes[0] == 0x30)
        {
            long len, header;
            if (bytes[1] < 0x80)
            {
                len = bytes[1];
                header = 2;
            }
            else
            {
                var n = bytes[1] & 0x7F;
                if (n is 0 or > 4 || bytes.Length < 2 + n) return bytes;
                len = 0;
                for (var i = 0; i < n; i++) len = (len << 8) | bytes[2 + i];
                header = 2 + n;
            }
            if (header + len <= bytes.Length) return bytes[..(int)(header + len)];
        }
        return bytes;
    }
}
