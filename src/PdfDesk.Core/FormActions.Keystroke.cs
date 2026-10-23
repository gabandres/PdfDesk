using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

/// <summary>
/// A keystroke script's verdict on one edit: whether it is accepted, and the text that replaces the selection
/// [<paramref name="SelStart"/>, <paramref name="SelEnd"/>) (scripts may rewrite what was typed, e.g. to insert a phone
/// number's separators), plus any alert texts.
/// </summary>
public sealed record KeystrokeResult(bool Accepted, string Change, int SelStart, int SelEnd, IReadOnlyList<string> Alerts);

/// <summary>
/// Runs a text field's keystroke script (/AA /K) on each edit while the user types (event.willCommit false), so
/// characters can be refused or transformed as typed. Opened once per editing session (off the UI thread: it reads
/// the document and starts the script engine); each call then takes well under a millisecond for the usual AF*
/// scripts. Scripts here can read the form but not change it, and nothing outward is reachable.
/// </summary>
public sealed class FieldKeystrokes : IDisposable
{
    private readonly IDisposable _doc;
    private readonly Func<string, string, int, int, KeystrokeResult?> _run;
    private readonly Lock _lock = new();

    internal FieldKeystrokes(IDisposable doc, Func<string, string, int, int, KeystrokeResult?> run)
    {
        _doc = doc;
        _run = run;
    }

    /// <summary>The field's keystroke check, or null when it has no keystroke script.</summary>
    public static FieldKeystrokes? Open(byte[] data, string? password, string fieldName) => FormEditor.OpenKeystrokes(data, password, fieldName);

    /// <summary>
    /// The script's verdict on replacing [<paramref name="selStart"/>, <paramref name="selEnd"/>) of
    /// <paramref name="value"/> with <paramref name="change"/> (empty for a deletion); null when the script couldn't
    /// run (then the edit should simply go through: the commit check still runs later).
    /// </summary>
    public KeystrokeResult? Run(string value, string change, int selStart, int selEnd)
    {
        lock (_lock) return _run(value, change, selStart, selEnd);
    }

    public void Dispose()
    {
        lock (_lock) _doc.Dispose();
    }
}

public static partial class FormEditor
{
    internal static FieldKeystrokes? OpenKeystrokes(byte[] data, string? password, string fieldName)
    {
        PdfDocument doc;
        try
        {
            doc = Open(data, password, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        try
        {
            var acro = PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
            var fields = new List<Field>();
            Collect(doc, acro, fields);
            if (fields.FirstOrDefault(f => f.Name == fieldName) is not { Type: "/Tx" or "/Ch" } field
                || ActionScript(field, "/K") is not { Length: > 0 } script)
            {
                doc.Dispose();
                return null;
            }
            var js = new JsFormHost(doc, fields, readOnly: true);
            return new FieldKeystrokes(doc, (value, change, selStart, selEnd) =>
            {
                selStart = Math.Clamp(selStart, 0, value.Length);
                selEnd = Math.Clamp(selEnd, selStart, value.Length);
                js.ResetBudget();
                if (js.Run("Keystroke", field, script, value, willCommit: false, change, selStart, selEnd) is not { } r) return null;
                var start = Math.Clamp(r.SelStart, 0, value.Length);
                var end = Math.Clamp(r.SelEnd, start, value.Length);
                return new KeystrokeResult(r.Rc, r.Change, start, end, r.Alerts);
            });
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }
}
