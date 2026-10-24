using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>
/// An edit of a signed PDF can't be appended as an incremental update (see <see cref="SignedEdits"/>): saving it means
/// rewriting the file, which invalidates its signatures. Nothing was changed; run the edit again through
/// <see cref="SignedEdits.Run"/> with the rewrite allowed once the user agrees.
/// </summary>
public sealed class SignaturesWouldBreakException(int signatureCount, string reason)
    : InvalidOperationException($"This edit can't be added to the signed file as an update ({reason}), so saving it would invalidate {(signatureCount == 1 ? "its signature" : $"its {signatureCount} signatures")}.")
{
    /// <summary>Signatures in the file (at least 1).</summary>
    public int SignatureCount { get; } = signatureCount;

    /// <summary>Why the edit can't be appended, for the log.</summary>
    public string Reason { get; } = reason;
}

/// <summary>What an edit run through <see cref="SignedEdits.Run"/> did to a signed document.</summary>
/// <param name="Appended">Saved as an incremental update: the signed bytes are kept as they were.</param>
/// <param name="RemovedItemKept">
/// The edit removed something (pages, a comment, an attachment) but was appended, so the removed item is still in the
/// file's earlier (signed) revision and can be recovered from it. <see cref="SignedEdits.Flatten"/> removes it for good.
/// </param>
public readonly record struct SignedEditReport(bool Appended, bool RemovedItemKept);

/// <summary>
/// Edits of signed documents are appended after the signed bytes, which keeps the signatures valid. When that isn't
/// possible, every Core edit throws <see cref="SignaturesWouldBreakException"/> rather than silently rewriting the file;
/// <see cref="Run"/> lets the caller allow the rewrite (after warning the user) and reports what the edit did.
/// </summary>
public static class SignedEdits
{
    private sealed class Scope
    {
        public bool AllowRewrite;
        public bool Appended;
        public bool Removed;
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>
    /// Runs <paramref name="edit"/> (synchronously, on this thread). With <paramref name="allowRewrite"/>, a signed
    /// document that can't be appended to is rewritten instead of throwing <see cref="SignaturesWouldBreakException"/>.
    /// </summary>
    public static (byte[] Data, SignedEditReport Report) Run(Func<byte[]> edit, bool allowRewrite = false)
    {
        var outer = Current.Value;
        var scope = new Scope { AllowRewrite = allowRewrite };
        Current.Value = scope;
        try
        {
            var data = edit();
            return (data, new SignedEditReport(scope.Appended, scope.Appended && scope.Removed));
        }
        finally
        {
            Current.Value = outer;
        }
    }

    /// <summary>
    /// Rewrites the document as a single revision (a "flattened copy"): earlier revisions, and whatever edits removed
    /// from them, are gone for good. Its signatures no longer verify.
    /// </summary>
    public static byte[] Flatten(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        return PdfObj.Save(doc, rewrite: true);
    }

    internal static bool RewriteAllowed => Current.Value?.AllowRewrite == true;

    /// <summary>
    /// For edits that always rewrite the file (encryption, optimizing, flattening): a signed document needs the caller's
    /// consent (<see cref="Run"/> with the rewrite allowed), else this throws.
    /// </summary>
    /// <exception cref="SignaturesWouldBreakException">The document is signed and the rewrite wasn't allowed.</exception>
    internal static void RequireRewrite(byte[] data, PdfSharp.Pdf.PdfDocument doc, string reason)
    {
        if (Incremental.LooksSigned(data) && !RewriteAllowed) throw new SignaturesWouldBreakException(Incremental.CountSignatures(doc), reason);
    }

    internal static void NoteAppended()
    {
        if (Current.Value is { } s) s.Appended = true;
    }

    /// <summary>Called by edits that remove content (pages, comments, attachments).</summary>
    internal static void NoteRemoval()
    {
        if (Current.Value is { } s) s.Removed = true;
    }
}
