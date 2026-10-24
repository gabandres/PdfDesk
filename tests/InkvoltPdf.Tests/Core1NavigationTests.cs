using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Tests;

/// <summary>Bookmarks, links, named destinations and form fields across page edits.</summary>
public class Core1NavigationTests
{
    private static readonly byte[] Nav = Core1TestPdf.Navigation();

    /// <summary>Outline as "Title@page" lines, children indented.</summary>
    private static string Outline(byte[] data)
    {
        using var e = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var sb = new StringBuilder();
        void Walk(IReadOnlyList<OutlineNode> nodes, string indent)
        {
            foreach (var n in nodes)
            {
                sb.Append($"{indent}{n.Title}@{n.PageIndex};");
                Walk(n.Children, indent + ">");
            }
        }
        Walk(e.GetOutline(), "");
        return sb.ToString();
    }

    private static int[] LinkTargets(byte[] data, int page)
    {
        using var e = PdfEngine.Open(data, InputFileTypes.PDF, null);
        return [.. e.GetLinks(page).Select(l => l.PageIndex)];
    }

    /// <summary>Page objects in the file: stray copies of removed or imported pages would show up here.</summary>
    private static int PageObjects(byte[] data) => Regex.Count(Encoding.Latin1.GetString(data), @"/Type\s*/Page(?![s\w])");

    private static PdfDictionary Catalog(PdfDocument doc) => doc.Internals.Catalog;

    private static PdfDictionary? Dict(PdfItem? item) => (item is PdfReference r ? r.Value : item) as PdfDictionary;

    private static string[] FieldNames(byte[] data)
    {
        using var doc = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Import);
        var fields = (Catalog(doc).Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields"))?.Elements;
        return fields is null ? [] : [.. fields.Select(f => Dict(f)!.Elements.GetString("/T"))];
    }

    [Fact]
    public void OriginalNavigationResolves()
    {
        Assert.Equal("Ch1@0;>Ch1.1@1;>Ch1.2@2;Ch2@3;>Ch2.1@4;", Outline(Nav));
        Assert.Equal([2, 3], LinkTargets(Nav, 0));
    }

    [Fact]
    public void DeleteDropsBookmarksLinksAndWidgetsOfRemovedPages()
    {
        var result = PageEditor.Delete(Nav, null, [1]);
        // Ch1.1 led to the deleted page (through a named destination); its siblings and the rest re-index.
        Assert.Equal("Ch1@0;>Ch1.2@1;Ch2@2;>Ch2.1@3;", Outline(result));
        Assert.Equal([1, 2], LinkTargets(result, 0));
        Assert.Equal(4, PageObjects(result));

        using var doc = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.Import);
        var root = Dict(Catalog(doc).Elements["/Outlines"])!;
        Assert.Equal(3, root.Elements.GetInteger("/Count")); // Ch1, Ch1.2, Ch2 (Ch2 is closed)
        var ch1 = Dict(root.Elements["/First"])!;
        Assert.Equal(1, ch1.Elements.GetInteger("/Count"));
        Assert.Equal(-1, Dict(ch1.Elements["/Next"])!.Elements.GetInteger("/Count"));
        var names = Dict(Dict(Catalog(doc).Elements["/Names"])!.Elements["/Dests"])!.Elements.GetArray("/Names")!;
        Assert.Equal(2, names.Elements.Count); // only "sec4" is left

        // The radio group keeps its surviving widget; the one on the deleted page is gone.
        var color = FormEditor.Read(result, null).Where(w => w.FieldName == "color").ToList();
        Assert.Single(color);
        Assert.Equal(1, color[0].PageIndex);
    }

    [Fact]
    public void DeletingAParentBookmarksPageKeepsItsChildren()
    {
        var result = PageEditor.Delete(Nav, null, [0]);
        Assert.Equal("Ch1.1@0;Ch1.2@1;Ch2@2;>Ch2.1@3;", Outline(result));
        Assert.Equal(4, PageObjects(result));
    }

    [Fact]
    public void ExtractKeepsOnlyNavigationIntoKeptPages()
    {
        var result = PageEditor.Extract(Nav, null, [3, 0]);
        Assert.Equal("Ch1@0;Ch2@1;", Outline(result));
        Assert.Equal([1], LinkTargets(result, 0)); // the link to page 3 went with it; "sec4" still resolves
        Assert.Equal(2, PageObjects(result));
        Assert.Equal(["name"], FieldNames(result));
        Assert.Throws<InvalidOperationException>(() => PageEditor.Extract(Nav, null, [9]));
    }

    [Fact]
    public void DeletingEveryBookmarkedPageRemovesTheOutline()
    {
        var result = PageEditor.Extract(TestPdf.Make(1), null, [0]);
        Assert.Equal("", Outline(result));
        var merged = PageEditor.Merge([new PdfSource(Nav, null, "a.pdf")], false);
        var none = PageEditor.Delete(merged, null, [0, 1, 2, 3]);
        Assert.Equal("Ch2.1@0;", Outline(none));
    }

    [Fact]
    public void MergeCarriesBookmarksLinksAndFields()
    {
        var merged = PageEditor.Merge([new PdfSource(Nav, null, "a.pdf"), new PdfSource(Nav, null, "b.pdf")], true);
        Assert.Equal("a@0;>Ch1@0;>>Ch1.1@1;>>Ch1.2@2;>Ch2@3;>>Ch2.1@4;b@5;>Ch1@5;>>Ch1.1@6;>>Ch1.2@7;>Ch2@8;>>Ch2.1@9;", Outline(merged));
        Assert.Equal([2, 3], LinkTargets(merged, 0));
        Assert.Equal([7, 8], LinkTargets(merged, 5));
        Assert.Equal(10, PageObjects(merged));
        Assert.Equal(["name", "color", "name_2", "color_2"], FieldNames(merged));

        var widgets = FormEditor.Read(merged, null);
        Assert.Equal(2, widgets.Count(w => w.FieldName == "color_2"));
        Assert.Equal("Alice", widgets.Single(w => w.FieldName == "name_2").Value);

        var flat = PageEditor.Merge([new PdfSource(Nav, null, "a.pdf"), new PdfSource(TestPdf.Make(1), null, "b.pdf")], false);
        Assert.Equal("Ch1@0;>Ch1.1@1;>Ch1.2@2;Ch2@3;>Ch2.1@4;", Outline(flat));
    }

    [Fact]
    public void InsertCarriesBookmarksForTheImportedPages()
    {
        var result = PageEditor.Insert(TestPdf.Make(2), null, new PdfSource(Nav, null, "nav.pdf"), [2, 0], 1);
        Assert.Equal([1, 3, 1, 2], TestPdf.PageNumbers(result));
        // Ch1.1, Ch2 and Ch2.1 lead to pages that weren't inserted.
        Assert.Equal("Ch1@2;>Ch1.2@1;", Outline(result));
        Assert.Equal([1], LinkTargets(result, 2)); // the "sec4" link had nowhere to go
        Assert.Equal(4, PageObjects(result));
        Assert.Equal(["color", "name"], FieldNames(result).Order());
        Assert.Single(FormEditor.Read(result, null), w => w.FieldName == "color");

        // Inserting into a document with bookmarks appends to them.
        var twice = PageEditor.Insert(result, null, new PdfSource(Nav, null, "nav.pdf"), [3], 4);
        Assert.Equal("Ch1@2;>Ch1.2@1;Ch2@4;", Outline(twice));
    }

    [Fact]
    public void DuplicateKeepsLinksAndMakesSeparateFields()
    {
        var result = PageEditor.Duplicate(Nav, null, [0]);
        Assert.Equal([3, 4], LinkTargets(result, 0));
        Assert.Equal([3, 4], LinkTargets(result, 1));
        Assert.Equal(6, PageObjects(result));
        Assert.Equal(["name", "color", "name_2"], FieldNames(result));
        Assert.Equal("Ch1@0;>Ch1.1@2;>Ch1.2@3;Ch2@4;>Ch2.1@5;", Outline(result));
    }

    [Fact]
    public void LinkAndBookmarkTargetsKeepTheirPosition()
    {
        using (var e = PdfEngine.Open(Nav, InputFileTypes.PDF, null))
        {
            var links = e.GetLinks(0);
            Assert.Equal(100, links[0].TargetY!.Value, 1.0); // /XYZ 0 692 on a 792pt page
            Assert.Null(links[1].TargetY); // /Fit names no position
            var outline = e.GetOutline();
            Assert.Equal(92, outline[0].TargetY!.Value, 1.0);
            Assert.Equal(292, outline[1].TargetY!.Value, 1.0); // /FitH 500
        }

        var merged = PageEditor.Merge([new PdfSource(TestPdf.Make(1), null, "a.pdf"), new PdfSource(Nav, null, "b.pdf")], true);
        using var m = PdfEngine.Open(merged, InputFileTypes.PDF, null);
        Assert.Equal(100, m.GetLinks(1)[0].TargetY!.Value, 1.0);
        Assert.Equal(92, m.GetOutline()[1].Children[0].TargetY!.Value, 1.0);
    }

    [Fact]
    public void LinkTargetYFollowsRotation()
    {
        var rotated = PageEditor.Rotate(Nav, null, [2], 90);
        using var e = PdfEngine.Open(rotated, InputFileTypes.PDF, null);
        // Page 3 turned clockwise: its PDF y = 692 (100pt below the top edge) is now 100pt from the left; the visible top is x = 0.
        var y = e.GetLinks(0)[0].TargetY;
        Assert.NotNull(y);
        Assert.InRange(y!.Value, 0, e.GetPage(2).Height);
    }

    [Fact]
    public void FieldsFromEachFileStayFillable()
    {
        var merged = PageEditor.Merge([new PdfSource(Nav, null, "a.pdf"), new PdfSource(Nav, null, "b.pdf")], false);
        var filled = FormEditor.SetValues(merged, null, new Dictionary<string, string> { ["name_2"] = "Bob" });
        var widgets = FormEditor.Read(filled, null);
        Assert.Equal("Alice", widgets.Single(w => w.FieldName == "name").Value);
        Assert.Equal("Bob", widgets.Single(w => w.FieldName == "name_2").Value);

        using var doc = PdfReader.Open(new MemoryStream(merged), PdfDocumentOpenMode.Import);
        var acro = Catalog(doc).Elements.GetDictionary("/AcroForm")!;
        Assert.Equal("/Helv 0 Tf 0 g", acro.Elements.GetString("/DA"));
        Assert.NotNull(acro.Elements.GetDictionary("/DR")?.Elements.GetDictionary("/Font")?.Elements["/Helv"]);
    }
}

/// <summary>XMP metadata kept in step with the Info dictionary.</summary>
public class Core1XmpTests
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";

    private static XDocument ReadXmp(byte[] data)
    {
        using var doc = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Import);
        var metadata = doc.Internals.Catalog.Elements.GetDictionary("/Metadata")!;
        return XDocument.Load(new MemoryStream(metadata.Stream.UnfilteredValue));
    }

    private static string? Prop(XDocument x, XName name) =>
        x.Descendants(Rdf + "Description").Select(d => d.Attribute(name)?.Value ?? d.Element(name)?.Value).FirstOrDefault(v => v is not null);

    [Fact]
    public void WriteMetadataUpdatesXmpWithEscaping()
    {
        var updated = PageEditor.WriteMetadata(TestPdf.Make(2), null, "Fish & <Chips>", "Ana \"A\"", "About", "k1, k2");
        var xmp = ReadXmp(updated);
        Assert.Equal("Fish & <Chips>", Prop(xmp, Dc + "title"));
        Assert.Equal("Ana \"A\"", Prop(xmp, Dc + "creator"));
        Assert.Equal("About", Prop(xmp, Dc + "description"));
        Assert.Equal("k1, k2", Prop(xmp, Pdf + "Keywords"));
        var modified = DateTimeOffset.Parse(Prop(xmp, Xmp + "ModifyDate")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(modified, DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now.AddMinutes(5));
        Assert.Equal(Prop(xmp, Xmp + "ModifyDate"), Prop(xmp, Xmp + "MetadataDate"));

        // The file is still sound for both libraries.
        Assert.Equal("Fish & <Chips>", PageEditor.ReadMetadata(updated, null).Title);
        Assert.Equal([1, 2], TestPdf.PageNumbers(updated));
    }

    [Fact]
    public void EditsKeepTheOriginalPacketAndUpdateIt()
    {
        var original = Core1TestPdf.WithXmp();
        var edited = PageEditor.WriteMetadata(original, null, "New title", "", "", "");
        var xmp = ReadXmp(edited);
        Assert.Equal("New title", Prop(xmp, Dc + "title"));
        Assert.Equal("keep me", Prop(xmp, (XNamespace)"urn:example:my" + "Custom"));
        Assert.Equal("2", Prop(xmp, (XNamespace)"http://www.aiim.org/pdfa/ns/id/" + "part"));
        Assert.Null(Prop(xmp, Dc + "creator")); // empty Info values leave no stale XMP behind
        Assert.StartsWith("2020-01-02", Prop(xmp, Xmp + "CreateDate"));
        Assert.StartsWith(DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), Prop(xmp, Xmp + "ModifyDate"));

        // Page edits refresh ModifyDate too.
        var rotated = PageEditor.Rotate(original, null, [0], 90);
        var x2 = ReadXmp(rotated);
        Assert.Equal("Old title", Prop(x2, Dc + "title"));
        Assert.StartsWith(DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), Prop(x2, Xmp + "ModifyDate"));
        using var engine = PdfEngine.Open(rotated, InputFileTypes.PDF, null);
        Assert.Contains("Page 1", engine.GetText(0).Text);
    }

    [Fact]
    public void EveryCoreSavePathKeepsTheXmpPacket()
    {
        var original = Core1TestPdf.WithXmp();
        var note = Annotations.NewId();
        var saves = new (string Name, Func<byte[], byte[]> Save)[]
        {
            ("note", d => Annotations.AddNote(d, null, 0, 50, 50, "Hi", "Me", id: note)),
            ("markup", d => Annotations.AddTextMarkup(d, null, 0, TextMarkupKind.Highlight, [new RectD(50, 50, 100, 12)], AnnotColor.Yellow, null, "Me")),
            ("contents", d => Annotations.SetContents(Annotations.AddNote(d, null, 0, 50, 50, "Hi", "Me", id: note), null, note, "Bye")),
            ("stamp", d => TextStamps.Put(d, null, 0, new RectD(60, 60, 80, 14), StampKind.Text, "Signed", 11, null)),
            ("attachment", d => Attachments.Add(d, null, "a.txt", [1, 2, 3])),
            ("redaction", d => Redaction.Apply(d, null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [new RectD(10, 10, 40, 40)] }, 72)),
        };
        var packet = ReadXmp(original).Root!.ToString(SaveOptions.DisableFormatting);
        var form = Core2Pdf.Form(["<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 700 300 720] /DA (/Helv 12 Tf 0 g) >>"],
            catalogExtra: "/Metadata 6 0 R", more: [Core2Pdf.Stream(packet, "/Type /Metadata /Subtype /XML")]);
        var filled = ReadXmp(FormEditor.SetValues(form, null, new Dictionary<string, string> { ["name"] = "Ana" }));
        Assert.Equal("keep me", Prop(filled, (XNamespace)"urn:example:my" + "Custom"));
        Assert.StartsWith("2020-01-02", Prop(filled, Xmp + "CreateDate"));
        foreach (var (name, save) in saves)
        {
            var xmp = ReadXmp(save(original));
            Assert.True(Prop(xmp, (XNamespace)"urn:example:my" + "Custom") == "keep me", $"{name}: custom XMP property lost");
            Assert.True(Prop(xmp, Dc + "title") == "Old title", $"{name}: title not kept");
            Assert.True(Prop(xmp, Xmp + "CreateDate")?.StartsWith("2020-01-02", StringComparison.Ordinal) == true, $"{name}: CreateDate changed");
        }
    }

    [Fact]
    public void MergedDocumentGetsAValidPacket()
    {
        var merged = PageEditor.Merge([new PdfSource(TestPdf.Make(1), null, "a & b.pdf")], true);
        var xmp = ReadXmp(merged);
        Assert.NotNull(Prop(xmp, Xmp + "ModifyDate"));
        Assert.Single(TestPdf.PageNumbers(merged));
    }
}
