using System.Text;
using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace InkvoltPdf.Tests;

/// <summary>Edits of encrypted PDFs keep the protection: method family, permissions and both passwords.</summary>
public class KeptProtectionTests
{
    private const string Owner = "owner-pw";
    private static readonly PdfPermissions Restricted = new(PrintPermission.LowResolution, Copy: false, Modify: true, Annotate: true, FillForms: true, Assemble: true);

    /// <summary>A one-page form (text field "name") plus a second page, encrypted by PDFsharp with <paramref name="method"/>.</summary>
    private static byte[] Encrypted(PdfDefaultEncryption method, string user, PdfPermissions? permissions = null)
    {
        permissions ??= Restricted;
        var form = Core2Pdf.Form(["<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 600 472 630] /DA (/Helv 12 Tf 0 g) /P 3 0 R >>"]);
        using var doc = PdfReader.Open(new MemoryStream(form), PdfDocumentOpenMode.Modify);
        doc.AddPage();
        if (method == PdfDefaultEncryption.V5) doc.SecurityHandler.SetEncryptionToV5(true);
        else doc.SecurityHandler.SetEncryption(method);
        var s = doc.SecuritySettings;
        s.UserPassword = user;
        s.OwnerPassword = Owner;
        s.PermitPrint = permissions.Print != PrintPermission.None;
        s.PermitFullQualityPrint = permissions.Print == PrintPermission.HighResolution;
        s.PermitExtractContent = permissions.Copy;
        s.PermitModifyDocument = permissions.Modify;
        s.PermitAnnotations = permissions.Annotate;
        s.PermitFormsFill = permissions.FillForms;
        s.PermitAssembleDocument = permissions.Assemble;
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    public static TheoryData<PdfDefaultEncryption, EncryptionMethod, EncryptionMethod> Methods => new()
    {
        { PdfDefaultEncryption.V1, EncryptionMethod.Rc4With40Bits, EncryptionMethod.Aes128 },
        { PdfDefaultEncryption.V2With40Bits, EncryptionMethod.Rc4With40Bits, EncryptionMethod.Aes128 },
        { PdfDefaultEncryption.V2With128Bits, EncryptionMethod.Rc4With128Bits, EncryptionMethod.Aes128 },
        { PdfDefaultEncryption.V4UsingAES, EncryptionMethod.Aes128, EncryptionMethod.Aes128 },
        { PdfDefaultEncryption.V5, EncryptionMethod.Aes256, EncryptionMethod.Aes256 },
    };

    private static IEnumerable<(string Name, Func<byte[], byte[]> Edit)> Edits() =>
    [
        ("rotate", d => PageEditor.Rotate(d, Owner, [0], 90)),
        ("fill", d => FormEditor.Fill(d, Owner, new Dictionary<string, string> { ["name"] = "Ada" }).Data),
        ("note", d => Annotations.AddNote(d, Owner, 0, 100, 100, "Kept", "Tester")),
    ];

    [Theory]
    [MemberData(nameof(Methods))]
    public void EditsKeepProtectionWithoutAnOpenPassword(PdfDefaultEncryption method, EncryptionMethod original, EncryptionMethod kept)
    {
        var pdf = Encrypted(method, "");
        Assert.Equal(original, PdfSecurity.ReadSecurity(pdf, null).Method);
        foreach (var (name, edit) in Edits())
        {
            var edited = edit(pdf);
            var info = PdfSecurity.ReadSecurity(edited, null);
            Assert.True(info.IsEncrypted, name);
            Assert.Equal(kept, info.Method);
            Assert.False(info.RequiresOpenPassword, name);
            Assert.Equal(Restricted, info.Permissions);
            using (var e = PdfEngine.Open(edited, InputFileTypes.PDF, null)) Assert.Equal(2, e.PageCount);
            // Still needs the owner password to edit, and it still works.
            Assert.Throws<OwnerPasswordRequiredException>(() => PageEditor.Rotate(edited, null, [1], 90));
            Assert.Throws<OwnerPasswordRequiredException>(() => PageEditor.Rotate(edited, "nope", [1], 90));
            PdfSecurity.ReadSecurity(PageEditor.Rotate(edited, Owner, [1], 90), null);
        }
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void EditsKeepTheOpenPassword(PdfDefaultEncryption method, EncryptionMethod original, EncryptionMethod kept)
    {
        var pdf = Encrypted(method, "user-pw");
        Assert.Equal(original, PdfSecurity.ReadSecurity(pdf, "user-pw").Method);
        foreach (var (name, edit) in Edits())
        {
            // AES-256 hides the open password from the owner: the caller has to supply it.
            var edited = kept == EncryptionMethod.Aes256
                ? KeptProtection.Run("user-pw", false, () => edit(pdf))
                : edit(pdf);
            Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(edited, InputFileTypes.PDF, null).Dispose());
            using (var e = PdfEngine.Open(edited, InputFileTypes.PDF, "user-pw")) Assert.Equal(2, e.PageCount);
            using (var e = PdfEngine.Open(edited, InputFileTypes.PDF, Owner)) Assert.Equal(2, e.PageCount);
            var info = PdfSecurity.ReadSecurity(edited, "user-pw");
            Assert.Equal(kept, info.Method);
            Assert.True(info.RequiresOpenPassword, name);
            Assert.Equal(Restricted, info.Permissions);
            Assert.True(PdfSecurity.IsOpenPassword(edited, "user-pw"));
            Assert.False(PdfSecurity.IsOpenPassword(edited, Owner));
        }
    }

    [Fact]
    public void TheEditIsInTheProtectedResult()
    {
        var pdf = Encrypted(PdfDefaultEncryption.V4UsingAES, "u");
        var filled = FormEditor.Fill(pdf, Owner, new Dictionary<string, string> { ["name"] = "Ada" }).Data;
        Assert.Equal("Ada", FormEditor.Read(filled, "u").Single(w => w.FieldName == "name").Value);
        var noted = Annotations.AddNote(filled, Owner, 0, 100, 100, "Kept", "Tester");
        Assert.Contains(Annotations.Read(noted, "u"), a => a.Contents == "Kept");
        var rotated = PageEditor.Rotate(noted, Owner, [0], 90);
        using var e = PdfEngine.Open(rotated, InputFileTypes.PDF, "u");
        Assert.True(e.GetPage(0).Width > e.GetPage(0).Height);
    }

    [Fact]
    public void Aes256WithAnUnknownOpenPasswordAsksOrDropsIt()
    {
        var pdf = Encrypted(PdfDefaultEncryption.V5, "secret");
        Assert.Throws<OpenPasswordUnknownException>(() => PageEditor.Rotate(pdf, Owner, [0], 90));

        var dropped = KeptProtection.Run(null, true, () => PageEditor.Rotate(pdf, Owner, [0], 90));
        var info = PdfSecurity.ReadSecurity(dropped, null);
        Assert.Equal(EncryptionMethod.Aes256, info.Method);
        Assert.False(info.RequiresOpenPassword);
        Assert.Equal(Restricted, info.Permissions);
        Assert.Throws<OwnerPasswordRequiredException>(() => PageEditor.Rotate(dropped, null, [0], 90));
    }

    [Theory]
    [InlineData(PdfDefaultEncryption.V1)]
    [InlineData(PdfDefaultEncryption.V2With40Bits)]
    [InlineData(PdfDefaultEncryption.V2With128Bits)]
    [InlineData(PdfDefaultEncryption.V4UsingAES)]
    public void RecoversTheUserPasswordFromTheOwnerPassword(PdfDefaultEncryption method)
    {
        // Permissions revision 2 can express, so V1 stays at revision 2.
        var pdf = Encrypted(method, "Zürich 42", new PdfPermissions(Copy: false));
        using var doc = PdfReader.Open(new MemoryStream(pdf), Owner, PdfDocumentOpenMode.Import);
        var h = doc.SecurityHandler;
        var revision = PdfObj.Int(h.Elements["/R"]) ?? 0;
        if (method == PdfDefaultEncryption.V1) Assert.Equal(2, revision);
        var o = Encoding.Latin1.GetBytes(PdfObj.Str(h.Elements["/O"])!);
        var length = revision == 2 ? 5 : (PdfObj.Int(h.Elements["/Length"]) ?? 40) / 8;
        // PDFsharp hashes only the key's bytes in the 50 rounds (it matters for 40-bit keys at revision 3).
        var found = new[] { false, true }.Select(shortRounds =>
            Encoding.Latin1.GetString(KeptProtection.UserPasswordFromOwner(Encoding.Latin1.GetBytes(Owner), revision, length, o, shortRounds)));
        Assert.Contains("Zürich 42", found);

        // And an edit keeps it (checked with PDFsharp: MuPDFCore can't pass non-ASCII passwords).
        var edited = PageEditor.Rotate(pdf, Owner, [0], 90);
        using var reopened = PdfReader.Open(new MemoryStream(edited), "Zürich 42", PdfDocumentOpenMode.Import);
        Assert.Equal(2, reopened.PageCount);
        Assert.ThrowsAny<Exception>(() => PdfReader.Open(new MemoryStream(edited), "Zurich 42", PdfDocumentOpenMode.Import).Dispose());
    }

    [Fact]
    public void ProtectionChangesAndExtractionAreNotUndone()
    {
        var pdf = Encrypted(PdfDefaultEncryption.V4UsingAES, "u");
        Assert.Equal(EncryptionMethod.None, PdfSecurity.ReadSecurity(PdfSecurity.Unprotect(pdf, Owner), null).Method);
        var changed = PdfSecurity.Protect(pdf, Owner, "new", "new-owner", PdfPermissions.All, EncryptionMethod.Aes128);
        var info = PdfSecurity.ReadSecurity(changed, "new");
        Assert.Equal(EncryptionMethod.Aes128, info.Method);
        Assert.True(info.Permissions.IsUnrestricted);
        Assert.Equal(EncryptionMethod.None, PdfSecurity.ReadSecurity(PageEditor.Extract(pdf, Owner, [1]), null).Method);
    }

    [Fact]
    public void SessionUndoRestoresThePasswords()
    {
        var plain = TestPdf.Make(2);
        using var session = DocumentSession.FromBytes(plain, "t.pdf");
        session.Commit(PdfSecurity.Protect(plain, null, "open", "own"), "Protect", "own", "open");
        Assert.Equal("own", session.Password);
        Assert.Equal("open", session.OpenPassword);
        session.Undo();
        Assert.Null(session.Password);
        Assert.False(session.Engine.IsEncrypted);
        session.Redo();
        Assert.Equal("own", session.Password);
        Assert.True(session.Engine.IsEncrypted);

        // An edit keeps the protection and the passwords.
        var rotated = KeptProtection.Run(session.OpenPassword, false, () => PageEditor.Rotate(session.Data, session.Password, [0], 90));
        session.Commit(rotated, "Rotate");
        Assert.True(PdfSecurity.ReadSecurity(session.Data, "open").RequiresOpenPassword);
    }
}
