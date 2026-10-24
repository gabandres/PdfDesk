using InkvoltPdf.Core;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Tests;

public class OwnerPasswordTests
{
    /// <summary>Opens without a password, but changes are restricted by an owner password.</summary>
    private static byte[] OwnerOnly(string owner)
    {
        using var doc = PdfReader.Open(new MemoryStream(TestPdf.Make(2)), PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.OwnerPassword = owner;
        doc.SecuritySettings.PermitModifyDocument = false;
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void EditsAskForTheOwnerPasswordAndWorkWithIt()
    {
        var data = OwnerOnly("own");
        using (var engine = PdfEngine.Open(data, MuPDFCore.InputFileTypes.PDF, null))
            Assert.Equal(2, engine.PageCount); // viewing needs no password

        var missing = Assert.Throws<OwnerPasswordRequiredException>(() => PageEditor.Rotate(data, null, [0], 90));
        Assert.False(missing.WrongPassword);
        var wrong = Assert.Throws<OwnerPasswordRequiredException>(() => PageEditor.Rotate(data, "nope", [0], 90));
        Assert.True(wrong.WrongPassword);
        Assert.Throws<OwnerPasswordRequiredException>(() => TextStamps.Put(data, null, 0, new RectD(10, 10, 50, 20), StampKind.Text, "x", 11));

        var rotated = PageEditor.Rotate(data, "own", [0], 90);
        using var after = PdfEngine.Open(rotated, MuPDFCore.InputFileTypes.PDF, "own");
        Assert.Equal(2, after.PageCount);
    }
}
