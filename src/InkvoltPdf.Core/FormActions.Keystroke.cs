using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

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

    internal FieldKeystrokes(IDisposable doc, bool warm, Func<string, string, int, int, KeystrokeResult?> run)
    {
        _doc = doc;
        Warm = warm;
        _run = run;
    }

    /// <summary>The script engine was started when this was opened (so the first keystroke doesn't wait for it).</summary>
    internal bool Warm { get; }

    /// <summary>
    /// The smallest edit that turns <paramref name="before"/> into <paramref name="after"/>: replace [Start, End) of
    /// <paramref name="before"/> with <paramref name="Change"/> (surrogate pairs are never split).
    /// </summary>
    public static (int Start, int End, string Change) Diff(string before, string after)
    {
        var max = Math.Min(before.Length, after.Length);
        var prefix = 0;
        while (prefix < max && before[prefix] == after[prefix]) prefix++;
        if (prefix > 0 && prefix < before.Length && char.IsHighSurrogate(before[prefix - 1])) prefix--;
        var suffix = 0;
        while (suffix < max - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        if (suffix > 0 && suffix < before.Length - prefix && char.IsLowSurrogate(before[^suffix])) suffix--;
        return (prefix, before.Length - suffix, after[prefix..(after.Length - suffix)]);
    }

    /// <summary>
    /// <paramref name="text"/> cut so that replacing [<paramref name="start"/>, <paramref name="end"/>) of
    /// <paramref name="value"/> with it stays within <paramref name="maxLength"/> characters (0 = no limit), without
    /// splitting a surrogate pair: a paste longer than the field's limit keeps what fits.
    /// </summary>
    public static string Fit(string value, int start, int end, string text, int maxLength)
    {
        if (maxLength <= 0) return text;
        var room = maxLength - (value.Length - Math.Max(0, end - start));
        if (text.Length <= room) return text;
        if (room <= 0) return "";
        if (char.IsHighSurrogate(text[room - 1])) room--;
        return text[..room];
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
            js.Prewarm(script); // engine, document scripts and the parsed script now, off the UI thread
            return new FieldKeystrokes(doc, js.Started, (value, change, selStart, selEnd) =>
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
