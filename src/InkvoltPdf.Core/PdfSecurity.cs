using System.Security.Cryptography;
using MuPDFCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>What printing a protected document allows.</summary>
public enum PrintPermission { None, LowResolution, HighResolution }

/// <summary>How a document is encrypted (standard security handler).</summary>
public enum EncryptionMethod { None, Rc4With40Bits, Rc4With128Bits, Aes128, Aes256, Unknown }

/// <summary>What a reader opening the document with the user (open) password may do.</summary>
/// <param name="Copy">Copy or extract text and graphics.</param>
/// <param name="Modify">Change the document's content (other than the operations below).</param>
/// <param name="Annotate">Add or change comments, and fill in form fields.</param>
/// <param name="FillForms">Fill in form fields (even when <paramref name="Annotate"/> is off).</param>
/// <param name="Assemble">Insert, rotate or delete pages and create bookmarks or thumbnails.</param>
public sealed record PdfPermissions(
    PrintPermission Print = PrintPermission.HighResolution,
    bool Copy = true,
    bool Modify = true,
    bool Annotate = true,
    bool FillForms = true,
    bool Assemble = true)
{
    /// <summary>Everything allowed.</summary>
    public static readonly PdfPermissions All = new();

    /// <summary>True when nothing is restricted.</summary>
    public bool IsUnrestricted => this == All;
}

/// <summary>A document's encryption.</summary>
/// <param name="Revision">The security handler's revision (/R), 0 when unencrypted.</param>
/// <param name="RequiresOpenPassword">Opening the document needs a password (a user password is set).</param>
/// <param name="Permissions">What the user password allows (everything when unencrypted).</param>
public sealed record SecurityInfo(EncryptionMethod Method, int Revision, bool RequiresOpenPassword, PdfPermissions Permissions)
{
    public bool IsEncrypted => Method != EncryptionMethod.None;
}

/// <summary>
/// Password protection: encrypts with AES-256 (PDF 2.0 standard security handler, revision 6), removes encryption, and
/// reports how a document is protected. Both edits rewrite the file, so a signed document needs the caller's consent
/// (<see cref="SignedEdits.Run"/> with the rewrite allowed), else they throw <see cref="SignaturesWouldBreakException"/>.
/// </summary>
public static class PdfSecurity
{
    // /P bits (ISO 32000-2 table 22), 1-based bit positions.
    private const int BitPrint = 1 << 2, BitModify = 1 << 3, BitCopy = 1 << 4, BitAnnotate = 1 << 5, BitFillForms = 1 << 8,
        BitAssemble = 1 << 10, BitHighPrint = 1 << 11;

    /// <summary>
    /// Encrypts the document with AES-256. At least one password must be given. Without an
    /// <paramref name="ownerPassword"/>, a document with restricted <paramref name="permissions"/> gets a random one
    /// (nobody can lift the restrictions), else the user password doubles as the owner password.
    /// </summary>
    /// <param name="password">The document's current password, if it is already encrypted (its owner password).</param>
    /// <param name="userPassword">Needed to open the document; null or empty = opens without one.</param>
    /// <param name="ownerPassword">Needed to change the permissions or the protection.</param>
    /// <exception cref="ArgumentException">Neither password was given.</exception>
    /// <exception cref="OwnerPasswordRequiredException">The document is encrypted and <paramref name="password"/> isn't its owner password.</exception>
    /// <exception cref="SignaturesWouldBreakException">The document is signed and the rewrite wasn't allowed.</exception>
    /// <param name="method">AES-256 (default) or AES-128 (for readers older than Acrobat 9).</param>
    public static byte[] Protect(byte[] data, string? password, string? userPassword, string? ownerPassword, PdfPermissions? permissions = null,
        EncryptionMethod method = EncryptionMethod.Aes256)
    {
        permissions ??= PdfPermissions.All;
        if (string.IsNullOrEmpty(userPassword) && string.IsNullOrEmpty(ownerPassword))
            throw new ArgumentException("Protecting a PDF needs an open password or a permissions password.", nameof(ownerPassword));
        if (method is not (EncryptionMethod.Aes256 or EncryptionMethod.Aes128))
            throw new ArgumentException("Inkvolt PDF encrypts with AES-256 or AES-128.", nameof(method));
        if (string.IsNullOrEmpty(ownerPassword))
            ownerPassword = permissions.IsUnrestricted ? userPassword! : Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        KeptProtection.Forget(doc);
        SignedEdits.RequireRewrite(data, doc, "encrypting rewrites the whole file");
        KeptProtection.Apply(doc, method, userPassword ?? "", ownerPassword, permissions);
        doc.Info.ModificationDate = DateTime.Now;
        return PdfObj.Save(doc, rewrite: true);
    }

    /// <summary>Removes the document's encryption (and so its passwords and permission restrictions).</summary>
    /// <param name="password">The owner (permissions) password; the user password suffices only when both are the same.</param>
    /// <exception cref="OwnerPasswordRequiredException">The password given isn't the owner password.</exception>
    /// <exception cref="SignaturesWouldBreakException">The document is signed and the rewrite wasn't allowed.</exception>
    public static byte[] Unprotect(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (!IsEncrypted(doc)) return data;
        KeptProtection.Forget(doc);
        SignedEdits.RequireRewrite(data, doc, "removing the encryption rewrites the whole file");
        doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        doc.Info.ModificationDate = DateTime.Now;
        return PdfObj.Save(doc, rewrite: true);
    }

    /// <summary>How the document is protected: method, revision, whether opening needs a password, and permissions.</summary>
    /// <param name="password">Needed only when the document has a user (open) password.</param>
    /// <exception cref="PasswordRequiredException">The document needs an open password and it is missing or wrong.</exception>
    public static SecurityInfo ReadSecurity(byte[] data, string? password)
    {
        var needsPassword = NeedsOpenPassword(data);
        if (needsPassword && string.IsNullOrEmpty(password)) throw new PasswordRequiredException(false);
        PdfDocument doc;
        try
        {
            doc = PdfObj.Open(data, needsPassword ? password : null, PdfDocumentOpenMode.Import);
        }
        catch (InvalidOperationException) when (needsPassword)
        {
            throw new PasswordRequiredException(true);
        }
        using (doc)
        {
            if (!IsEncrypted(doc)) return new SecurityInfo(EncryptionMethod.None, 0, false, PdfPermissions.All);
            var h = doc.SecurityHandler;
            var v = PdfObj.Int(h.Elements["/V"]) ?? 0;
            var r = PdfObj.Int(h.Elements["/R"]) ?? 0;
            var length = PdfObj.Int(h.Elements["/Length"]) ?? 40;
            var method = PdfObj.Name(h.Elements["/Filter"]) != "Standard" ? EncryptionMethod.Unknown : v switch
            {
                1 => EncryptionMethod.Rc4With40Bits,
                2 or 3 => length <= 40 ? EncryptionMethod.Rc4With40Bits : EncryptionMethod.Rc4With128Bits,
                4 => CryptFilterMethod(h),
                5 => EncryptionMethod.Aes256,
                _ => EncryptionMethod.Unknown,
            };
            return new SecurityInfo(method, r, needsPassword, Permissions(PdfObj.Num(h.Elements["/P"], -1), r));
        }
    }

    /// <summary>True when <paramref name="password"/> is the encrypted document's open (user) password, not just its owner password.</summary>
    public static bool IsOpenPassword(byte[] data, string? password) =>
        !string.IsNullOrEmpty(password) && NeedsOpenPassword(data) && KeptProtection.OpensAsUser(data, password);

    /// <summary>The permissions a /P value grants under security handler revision <paramref name="revision"/>.</summary>
    internal static PdfPermissions Permissions(double p, int revision)
    {
        var bits = unchecked((int)(long)p);
        bool Has(int bit) => (bits & bit) != 0;
        var print = !Has(BitPrint) ? PrintPermission.None
            : revision >= 3 && !Has(BitHighPrint) ? PrintPermission.LowResolution : PrintPermission.HighResolution;
        // Revision 2 has no separate form-fill and assembly bits: annotate and modify cover them.
        return revision >= 3
            ? new PdfPermissions(print, Has(BitCopy), Has(BitModify), Has(BitAnnotate), Has(BitFillForms), Has(BitAssemble))
            : new PdfPermissions(print, Has(BitCopy), Has(BitModify), Has(BitAnnotate), Has(BitAnnotate), Has(BitModify));
    }

    private static EncryptionMethod CryptFilterMethod(PdfDictionary handler)
    {
        var name = PdfObj.Name(handler.Elements["/StmF"]) ?? "Identity";
        if (name == "Identity") return EncryptionMethod.None;
        var filter = PdfObj.Dict(PdfObj.Dict(handler.Elements["/CF"])?.Elements["/" + name]);
        return PdfObj.Name(filter?.Elements["/CFM"]) switch
        {
            "AESV2" => EncryptionMethod.Aes128,
            "AESV3" => EncryptionMethod.Aes256,
            "V2" => EncryptionMethod.Rc4With128Bits,
            _ => EncryptionMethod.Unknown,
        };
    }

    private static bool IsEncrypted(PdfDocument doc) =>
        doc.SecurityHandler.Elements.ContainsKey("/Filter");

    /// <summary>MuPDF tells cheaply whether the empty user password opens the document.</summary>
    internal static bool NeedsOpenPassword(byte[] data)
    {
        try
        {
            using var ctx = new MuPDFContext(8 * 1024 * 1024);
            var doc = new MuPDFDocument(ctx, data, InputFileTypes.PDF);
            try
            {
                return doc.EncryptionState == EncryptionState.Encrypted;
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
