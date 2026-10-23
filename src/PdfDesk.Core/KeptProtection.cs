using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MuPDFCore;
using PdfSharp.Pdf;

namespace PdfDesk.Core;

/// <summary>
/// An edit of an AES-256 document opened with its permissions (owner) password: the document also has an open (user)
/// password, which can't be worked out from the owner password at that revision. Nothing was changed; run the edit again
/// through <see cref="KeptProtection.Run{T}"/> with the open password, or with <c>dropOpenPassword</c> to save the
/// document without one (its permissions password and restrictions stay).
/// </summary>
public sealed class OpenPasswordUnknownException()
    : InvalidOperationException("This PDF needs a password to open, and the edit has to keep it. Enter the open password, or save without it.");

/// <summary>
/// Keeps an encrypted document encrypted through edits. PDFsharp decrypts a document it opens for editing and saves it
/// without encryption; every Core edit opens through <see cref="PdfObj.Open"/> (which calls <see cref="Track"/>) and
/// saves through <see cref="PdfObj.Save"/> or the page editor (which call <see cref="Reapply"/>), so the edited
/// document gets the original protection back: AES-256 when it was AES-256, else AES-128 (RC4 is upgraded), the same
/// permissions, the owner password the edit was opened with, and the same open password. That one is the empty
/// password when the document opens without one; else, for revisions 2-4, it is recovered from the owner password
/// (ISO 32000-1 7.6.3.4, algorithm 7); for AES-256 it has to come from the caller (<see cref="Run{T}"/>), else the
/// edit throws <see cref="OpenPasswordUnknownException"/>. <see cref="PdfSecurity.Protect"/> and
/// <see cref="PdfSecurity.Unprotect"/> set the protection themselves, and extracting pages makes an unprotected document.
/// </summary>
public static class KeptProtection
{
    private sealed class Scope
    {
        public string? OpenPassword;
        public bool DropOpenPassword;
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>The protection a document had when it was opened for editing.</summary>
    private sealed record Original(EncryptionMethod Method, PdfPermissions Permissions, string OwnerPassword, string? UserPassword, bool EncryptMetadata);

    private static readonly ConditionalWeakTable<PdfDocument, Original> Tracked = new();

    /// <summary>
    /// Runs <paramref name="edit"/> (synchronously, on this thread) knowing the document's open password, for edits of an
    /// AES-256 document opened with its owner password. With <paramref name="dropOpenPassword"/>, such a document is saved
    /// without an open password instead.
    /// </summary>
    public static T Run<T>(string? openPassword, bool dropOpenPassword, Func<T> edit)
    {
        var outer = Current.Value;
        Current.Value = new Scope { OpenPassword = openPassword, DropOpenPassword = dropOpenPassword };
        try
        {
            return edit();
        }
        finally
        {
            Current.Value = outer;
        }
    }

    /// <summary>Remembers the protection of <paramref name="doc"/>, just opened for editing from <paramref name="data"/>.</summary>
    internal static void Track(PdfDocument doc, byte[] data, string? password)
    {
        var h = doc.SecurityHandler;
        if (!h.Elements.ContainsKey("/Filter")) return;
        var r = PdfObj.Int(h.Elements["/R"]) ?? 0;
        var v = PdfObj.Int(h.Elements["/V"]) ?? 0;
        var method = v == 5 || r >= 5 ? EncryptionMethod.Aes256 : EncryptionMethod.Aes128;
        var permissions = PdfSecurity.Permissions(PdfObj.Num(h.Elements["/P"], -1), r);
        var encryptMetadata = h.Elements["/EncryptMetadata"] is not PdfBoolean { Value: false };
        var owner = password ?? "";

        string? user;
        if (!PdfSecurity.NeedsOpenPassword(data)) user = "";
        else if (r <= 4 && RawBytes(h.Elements["/O"]) is { Length: >= 32 } o)
        {
            var length = r == 2 ? 5 : Math.Clamp((PdfObj.Int(h.Elements["/Length"]) ?? 40) / 8, 5, 16);
            // Some writers (PDFsharp among them) hash only the key's bytes in the 50 rounds: try the standard way first.
            var ownerBytes = Encoding.Latin1.GetBytes(owner);
            user = new[] { false, true }
                .Select(shortRounds => Encoding.Latin1.GetString(UserPasswordFromOwner(ownerBytes, r, length, o, shortRounds)))
                .FirstOrDefault(candidate => IsUserPassword(data, candidate)); // none: ask rather than guess
        }
        else user = null;
        Tracked.AddOrUpdate(doc, new Original(method, permissions, owner, user, encryptMetadata));
    }

    /// <summary>The edit sets the protection itself (protect, unprotect) or makes a new document (extract): don't restore it.</summary>
    internal static void Forget(PdfDocument doc) => Tracked.Remove(doc);

    /// <summary>
    /// Puts the original protection back on <paramref name="doc"/> before it is written (no-op for a document that wasn't
    /// encrypted).
    /// </summary>
    /// <exception cref="OpenPasswordUnknownException">AES-256 with an open password that isn't known.</exception>
    internal static void Reapply(PdfDocument doc)
    {
        if (!Tracked.TryGetValue(doc, out var o)) return;
        var scope = Current.Value;
        var user = o.UserPassword;
        if (user is null)
        {
            if (scope?.DropOpenPassword == true) user = "";
            else if (!string.IsNullOrEmpty(scope?.OpenPassword)) user = scope.OpenPassword;
            else throw new OpenPasswordUnknownException();
        }
        var owner = o.OwnerPassword;
        if (owner.Length == 0)
            owner = user.Length > 0 && o.Permissions.IsUnrestricted ? user : Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        Apply(doc, o.Method, user, owner, o.Permissions, o.EncryptMetadata);
    }

    /// <summary>Sets the document's encryption (to be written by the next save).</summary>
    internal static void Apply(PdfDocument doc, EncryptionMethod method, string userPassword, string ownerPassword, PdfPermissions permissions, bool encryptMetadata = true)
    {
        var s = doc.SecuritySettings;
        if (method == EncryptionMethod.Aes128) doc.SecurityHandler.SetEncryptionToV4UsingAES(encryptMetadata);
        else doc.SecurityHandler.SetEncryptionToV5(encryptMetadata);
        s.UserPassword = userPassword;
        s.OwnerPassword = ownerPassword;
        s.PermitPrint = permissions.Print != PrintPermission.None;
        s.PermitFullQualityPrint = permissions.Print == PrintPermission.HighResolution;
        s.PermitExtractContent = permissions.Copy;
        s.PermitModifyDocument = permissions.Modify;
        s.PermitAnnotations = permissions.Annotate;
        s.PermitFormsFill = permissions.FillForms;
        s.PermitAssembleDocument = permissions.Assemble;
    }

    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>
    /// The user password of a standard security handler (revision 2-4) from its owner password and /O value: algorithm 7
    /// of ISO 32000-1 (7.6.3.4), whose result is the padded user password, with the padding taken off.
    /// </summary>
    /// <param name="keyLength">Key length in bytes: 5 for revision 2, else /Length / 8.</param>
    /// <param name="shortRounds">Hash only the key's first bytes in the 50 extra rounds (as algorithm 2 does), not all 16.</param>
    internal static byte[] UserPasswordFromOwner(byte[] ownerPassword, int revision, int keyLength, byte[] o, bool shortRounds = false)
    {
        // Algorithm 3, steps a-d: the RC4 key from the padded owner password.
        var padded = new byte[32];
        var n = Math.Min(32, ownerPassword.Length);
        Array.Copy(ownerPassword, padded, n);
        Array.Copy(Padding, 0, padded, n, 32 - n);
        var hash = MD5.HashData(padded);
        if (revision >= 3)
            for (var i = 0; i < 50; i++) hash = MD5.HashData(shortRounds ? hash.AsSpan(0, keyLength) : hash);
        var key = hash.AsSpan(0, revision == 2 ? 5 : keyLength).ToArray();

        // Algorithm 7, step b: decrypt /O (revision 3+: 20 times, with the key XORed with 19 down to 0).
        var value = o.AsSpan(0, 32).ToArray();
        if (revision == 2) value = Rc4(key, value);
        else
        {
            var k = new byte[key.Length];
            for (var i = 19; i >= 0; i--)
            {
                for (var j = 0; j < key.Length; j++) k[j] = (byte)(key[j] ^ i);
                value = Rc4(k, value);
            }
        }

        // The user password is followed by the start of the padding string.
        for (var len = 0; len <= 32; len++)
            if (value.AsSpan(len).SequenceEqual(Padding.AsSpan(0, 32 - len))) return value[..len];
        return value;
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var result = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return result;
    }

    /// <summary>A string's bytes as stored in the file (encryption dictionary strings are never encrypted).</summary>
    private static byte[]? RawBytes(PdfItem? item)
    {
        if (PdfObj.Resolve(item) is not PdfString s) return null;
        return s.Encoding == PdfStringEncoding.Unicode
            ? [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(s.Value)]
            : Encoding.Latin1.GetBytes(s.Value);
    }

    /// <summary>
    /// True when a recovered password opens the document. MuPDF is quick; non-ASCII passwords are checked by PDFsharp
    /// instead (MuPDFCore can't pass them on).
    /// </summary>
    private static bool IsUserPassword(byte[] data, string password)
    {
        if (password.All(char.IsAscii)) return OpensAsUser(data, password);
        try
        {
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(data, writable: false), password, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            return true;
        }
        catch (Exception ex) when (ex is PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="password"/> opens the document as its user (open) password.</summary>
    internal static bool OpensAsUser(byte[] data, string password)
    {
        try
        {
            using var ctx = new MuPDFContext(8 * 1024 * 1024);
            var doc = new MuPDFDocument(ctx, data, InputFileTypes.PDF);
            try
            {
                return doc.EncryptionState != EncryptionState.Encrypted || doc.TryUnlock(password, out var kind) && kind.HasFlag(PasswordTypes.User);
            }
            finally
            {
                PdfEngine.DisposeDocument(doc);
            }
        }
        catch (Exception ex) when (ex is MuPDFException or ArgumentException)
        {
            return false;
        }
    }
}
