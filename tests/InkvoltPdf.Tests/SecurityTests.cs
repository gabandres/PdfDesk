using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace InkvoltPdf.Tests;

public class SecurityTests
{
    [Fact]
    public void ProtectWithAes256OpensWithThePasswordOnly()
    {
        var pdf = PdfSecurity.Protect(TestPdf.Make(2), null, "open", "owner");
        Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(pdf, InputFileTypes.PDF, null).Dispose());
        Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(pdf, InputFileTypes.PDF, "wrong").Dispose());
        using (var engine = PdfEngine.Open(pdf, InputFileTypes.PDF, "open"))
        {
            Assert.Equal(2, engine.PageCount);
            Assert.Contains("Page 1 alpha", engine.GetText(0).Text);
        }
        using (var engine = PdfEngine.Open(pdf, InputFileTypes.PDF, "owner")) Assert.Equal(2, engine.PageCount);

        var info = PdfSecurity.ReadSecurity(pdf, "open");
        Assert.Equal(EncryptionMethod.Aes256, info.Method);
        Assert.True(info.RequiresOpenPassword);
        Assert.True(info.Permissions.IsUnrestricted);
        Assert.Throws<PasswordRequiredException>(() => PdfSecurity.ReadSecurity(pdf, null));
        Assert.Throws<PasswordRequiredException>(() => PdfSecurity.ReadSecurity(pdf, "wrong"));
    }

    [Fact]
    public void PermissionsAreWrittenAndReadBack()
    {
        var wanted = new PdfPermissions(PrintPermission.LowResolution, Copy: false, Modify: false, Annotate: true, FillForms: true, Assemble: false);
        var pdf = PdfSecurity.Protect(TestPdf.Make(1), null, null, "owner", wanted);
        var info = PdfSecurity.ReadSecurity(pdf, null);
        Assert.Equal(EncryptionMethod.Aes256, info.Method);
        Assert.False(info.RequiresOpenPassword);
        Assert.Equal(wanted, info.Permissions);
        Assert.True(info.Revision >= 5);

        // /P as stored: print (bit 3) on, high-quality print (bit 12) off, copy (bit 5) off.
        using var doc = Core2Pdf.Open(pdf);
        var p = (int)PdfObj.Num(doc.SecurityHandler.Elements["/P"]);
        Assert.NotEqual(0, p & 4);
        Assert.Equal(0, p & 2048);
        Assert.Equal(0, p & 16);
        Assert.NotEqual(0, p & 32);

        var none = PdfSecurity.Protect(TestPdf.Make(1), null, "u", null, new PdfPermissions(PrintPermission.None, false, false, false, false, false));
        Assert.Equal(PrintPermission.None, PdfSecurity.ReadSecurity(none, "u").Permissions.Print);
        // A random owner password was made: the user password can't lift the restrictions.
        Assert.Throws<OwnerPasswordRequiredException>(() => PdfSecurity.Unprotect(none, "u"));
    }

    [Fact]
    public void UnprotectNeedsTheOwnerPassword()
    {
        var pdf = PdfSecurity.Protect(TestPdf.Make(1), null, "open", "owner");
        Assert.Throws<OwnerPasswordRequiredException>(() => PdfSecurity.Unprotect(pdf, "open"));
        Assert.Throws<OwnerPasswordRequiredException>(() => PdfSecurity.Unprotect(pdf, "nope"));

        // The owner can change the protection.
        var changed = PdfSecurity.Protect(pdf, "owner", "new-open", "new-owner");
        Assert.Throws<PasswordRequiredException>(() => PdfSecurity.ReadSecurity(changed, "open"));
        Assert.Equal(EncryptionMethod.Aes256, PdfSecurity.ReadSecurity(changed, "new-open").Method);

        var plain = PdfSecurity.Unprotect(pdf, "owner");
        Assert.Equal(EncryptionMethod.None, PdfSecurity.ReadSecurity(plain, null).Method);
        using var engine = PdfEngine.Open(plain, InputFileTypes.PDF, null);
        Assert.False(engine.IsEncrypted);
        Assert.Contains("Page 1 alpha", engine.GetText(0).Text);
    }

    [Fact]
    public void ReadsOlderMethods()
    {
        var unencrypted = PdfSecurity.ReadSecurity(TestPdf.Make(1), null);
        Assert.Equal(EncryptionMethod.None, unencrypted.Method);
        Assert.False(unencrypted.RequiresOpenPassword);

        // PDFsharp's default encryption (TestPdf.Encrypted) is AES-128.
        var aes = PdfSecurity.ReadSecurity(TestPdf.Encrypted(1, "pw"), "pw");
        Assert.Equal(EncryptionMethod.Aes128, aes.Method);
        Assert.True(aes.RequiresOpenPassword);

        Assert.Equal(EncryptionMethod.Rc4With40Bits, PdfSecurity.ReadSecurity(With(PdfDefaultEncryption.V2With40Bits), "pw").Method);
        Assert.Equal(EncryptionMethod.Rc4With128Bits, PdfSecurity.ReadSecurity(With(PdfDefaultEncryption.V2With128Bits), "pw").Method);
        Assert.Equal(EncryptionMethod.Rc4With128Bits, PdfSecurity.ReadSecurity(With(PdfDefaultEncryption.V4UsingRC4), "pw").Method);

        static byte[] With(PdfDefaultEncryption method)
        {
            using var doc = PdfReader.Open(new MemoryStream(TestPdf.Make(1)), PdfDocumentOpenMode.Modify);
            doc.SecurityHandler.SetEncryption(method);
            doc.SecuritySettings.UserPassword = "pw";
            doc.SecuritySettings.OwnerPassword = "owner";
            using var ms = new MemoryStream();
            doc.Save(ms, false);
            return ms.ToArray();
        }
    }

    [Fact]
    public void SignedFilesNeedConsentToEncrypt()
    {
        var signed = SignedEditTests.Signed();
        Assert.Throws<SignaturesWouldBreakException>(() => PdfSecurity.Protect(signed, null, "x", "y"));
        var (pdf, report) = SignedEdits.Run(() => PdfSecurity.Protect(signed, null, "x", "y"), allowRewrite: true);
        Assert.False(report.Appended);
        Assert.Equal(EncryptionMethod.Aes256, PdfSecurity.ReadSecurity(pdf, "x").Method);
    }
}
