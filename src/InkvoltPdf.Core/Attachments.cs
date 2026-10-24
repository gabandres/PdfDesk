using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

public enum AttachmentSource { Document, Annotation }

/// <summary>A file embedded in the PDF.</summary>
/// <param name="Key">Pass to <see cref="Attachments.Extract"/> / <see cref="Attachments.Remove"/>: "doc:&lt;name&gt;" for the
/// document's attachment list, "annot:&lt;annotation id&gt;" for a file-attachment annotation.</param>
/// <param name="Size">Uncompressed size in bytes.</param>
/// <param name="PageIndex">Page of the file-attachment annotation, or -1 for document attachments.</param>
public sealed record AttachmentInfo(
    string Key,
    string FileName,
    string? Description,
    long Size,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    AttachmentSource Source,
    int PageIndex,
    string? MimeType);

/// <summary>Embedded files: the document's /EmbeddedFiles name tree and file-attachment annotations.</summary>
public static class Attachments
{
    private const string DocPrefix = "doc:", AnnotPrefix = "annot:";

    public static IReadOnlyList<AttachmentInfo> List(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF's attachments can't be read");
        var list = new List<AttachmentInfo>();
        foreach (var (key, value) in PdfObj.NameTree(EmbeddedFilesRoot(doc)))
            if (PdfObj.Dict(value) is { } fs) list.Add(Describe(DocPrefix + key, key, fs, AttachmentSource.Document, -1));
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
                if (PdfObj.Dict(annots.Elements[i]) is { } a && PdfObj.NameOf(a, "/Subtype") == "/FileAttachment" && PdfObj.Dict(a.Elements["/FS"]) is { } fs)
                    list.Add(Describe(AnnotPrefix + Annotations.IdOf(a, p, i), "attachment", fs, AttachmentSource.Annotation, p));
        }
        return list;
    }

    /// <summary>The attachment's file contents (decoded).</summary>
    public static byte[] Extract(byte[] data, string? password, string key)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF's attachments can't be read");
        var fs = FindSpec(doc, key) ?? throw new InvalidOperationException("That attachment no longer exists.");
        var stream = FileStream(fs) ?? throw new InvalidOperationException("That attachment has no embedded data.");
        return PdfObj.StreamBytes(stream);
    }

    /// <summary>Embeds a file in the document's attachment list (renamed "name (2).ext" if the name is taken).</summary>
    public static byte[] Add(byte[] data, string? password, string fileName, byte[] bytes, string? description = null, DateTimeOffset? modified = null)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var entries = PdfObj.NameTree(EmbeddedFilesRoot(doc)).ToList();
        var name = Path.GetFileName(fileName);
        if (name.Length == 0) name = "attachment";
        var unique = name;
        for (var n = 2; entries.Any(e => e.Key == unique); n++)
            unique = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";

        var now = DateTimeOffset.Now;
        var stream = PdfObj.AddStream(doc, bytes);
        stream.Elements["/Type"] = new PdfName("/EmbeddedFile");
        if (MimeOf(name) is { } mime) stream.Elements["/Subtype"] = new PdfName("/" + mime.Replace("/", "#2F"));
        var prms = new PdfDictionary(doc);
        prms.Elements["/Size"] = new PdfInteger(bytes.Length);
        prms.Elements["/CreationDate"] = PdfObj.DateString(now);
        prms.Elements["/ModDate"] = PdfObj.DateString(modified ?? now);
        prms.Elements["/CheckSum"] = new PdfString(System.Text.Encoding.Latin1.GetString(MD5.HashData(bytes)), PdfStringEncoding.RawEncoding);
        stream.Elements["/Params"] = prms;

        var ef = new PdfDictionary(doc);
        ef.Elements["/F"] = stream.Reference!;
        ef.Elements["/UF"] = stream.Reference!;
        var spec = new PdfDictionary(doc);
        spec.Elements["/Type"] = new PdfName("/Filespec");
        spec.Elements["/F"] = new PdfString(new string([.. unique.Select(c => c < 128 ? c : '_')]));
        spec.Elements["/UF"] = PdfObj.MakeString(unique);
        if (!string.IsNullOrEmpty(description)) spec.Elements["/Desc"] = PdfObj.MakeString(description);
        spec.Elements["/EF"] = ef;
        spec.Elements["/AFRelationship"] = new PdfName("/Unspecified");
        doc.Internals.AddObject(spec);

        entries.Add((unique, spec.Reference));
        WriteTree(doc, entries);
        return PdfObj.Save(doc);
    }

    /// <summary>Removes a document attachment, or a file-attachment annotation.</summary>
    public static byte[] Remove(byte[] data, string? password, string key)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (key.StartsWith(AnnotPrefix, StringComparison.Ordinal))
        {
            if (!Annotations.DeleteIn(doc, key[AnnotPrefix.Length..])) throw new InvalidOperationException("That attachment no longer exists.");
        }
        else
        {
            var name = key.StartsWith(DocPrefix, StringComparison.Ordinal) ? key[DocPrefix.Length..] : key;
            var entries = PdfObj.NameTree(EmbeddedFilesRoot(doc)).ToList();
            if (entries.RemoveAll(e => e.Key == name) == 0) throw new InvalidOperationException("That attachment no longer exists.");
            WriteTree(doc, entries);
            SignedEdits.NoteRemoval();
        }
        return PdfObj.Save(doc);
    }

    private static PdfDictionary? EmbeddedFilesRoot(PdfDocument doc) =>
        PdfObj.Dict(PdfObj.Dict(doc.Internals.Catalog.Elements["/Names"])?.Elements["/EmbeddedFiles"]);

    /// <summary>Rewrites the /EmbeddedFiles name tree as one sorted leaf (or drops it when empty).</summary>
    private static void WriteTree(PdfDocument doc, List<(string Key, PdfItem? Value)> entries)
    {
        var catalog = doc.Internals.Catalog;
        var names = PdfObj.Dict(catalog.Elements["/Names"]);
        if (names is null)
        {
            if (entries.Count == 0) return;
            names = new PdfDictionary(doc);
            catalog.Elements["/Names"] = names;
        }
        if (entries.Count == 0)
        {
            names.Elements.Remove("/EmbeddedFiles");
            return;
        }
        var arr = new PdfArray(doc);
        foreach (var (k, v) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            arr.Elements.Add(PdfObj.MakeString(k));
            arr.Elements.Add(v ?? PdfNull.Value);
        }
        var tree = new PdfDictionary(doc);
        tree.Elements["/Names"] = arr;
        names.Elements["/EmbeddedFiles"] = tree;
    }

    private static PdfDictionary? FindSpec(PdfDocument doc, string key)
    {
        if (key.StartsWith(AnnotPrefix, StringComparison.Ordinal))
            return Annotations.Find(doc, key[AnnotPrefix.Length..]) is { } f ? PdfObj.Dict(f.Annot.Elements["/FS"]) : null;
        var name = key.StartsWith(DocPrefix, StringComparison.Ordinal) ? key[DocPrefix.Length..] : key;
        return PdfObj.NameTree(EmbeddedFilesRoot(doc)).Where(e => e.Key == name).Select(e => PdfObj.Dict(e.Value)).FirstOrDefault();
    }

    private static PdfDictionary? FileStream(PdfDictionary fs)
    {
        var ef = PdfObj.Dict(fs.Elements["/EF"]);
        return PdfObj.Dict(ef?.Elements["/UF"]) ?? PdfObj.Dict(ef?.Elements["/F"]);
    }

    private static AttachmentInfo Describe(string key, string fallbackName, PdfDictionary fs, AttachmentSource source, int page)
    {
        var name = PdfObj.Str(fs.Elements["/UF"]) ?? PdfObj.Str(fs.Elements["/F"]) ?? fallbackName;
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.Length == 0) name = fallbackName;
        var stream = FileStream(fs);
        var prms = PdfObj.Dict(stream?.Elements["/Params"]);
        long size = PdfObj.Int(prms?.Elements["/Size"]) ?? -1;
        if (size < 0) size = stream is null ? 0 : PdfObj.StreamBytes(stream).LongLength;
        var mime = PdfObj.Name(stream?.Elements["/Subtype"])?.Replace("#2F", "/").Replace("#2f", "/");
        return new AttachmentInfo(key, name, PdfObj.Str(fs.Elements["/Desc"]), size,
            PdfObj.ParseDate(PdfObj.Str(prms?.Elements["/CreationDate"])), PdfObj.ParseDate(PdfObj.Str(prms?.Elements["/ModDate"])),
            source, page, mime);
    }

    private static string? MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".xml" => "text/xml",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".zip" => "application/zip",
        _ => null,
    };
}
