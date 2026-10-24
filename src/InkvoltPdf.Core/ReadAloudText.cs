using System.Text;

namespace InkvoltPdf.Core;

/// <summary>
/// A paragraph to read aloud: its spoken text (lines joined, hyphenation undone) and, for each spoken character, where
/// it came from in the page text, so what the voice says can be highlighted on the page.
/// </summary>
public sealed record SpeechChunk(int Page, string Text, int[] Source)
{
    /// <summary>The page-text range [Start, End) the spoken characters [start, end) came from.</summary>
    public (int Start, int End) SourceRange(int start, int end)
    {
        if (Source.Length == 0) return (0, 0);
        start = Math.Clamp(start, 0, Source.Length - 1);
        end = Math.Clamp(end, start + 1, Source.Length);
        return (Source[start], Source[end - 1] + 1);
    }

    /// <summary>The page-text index where the chunk starts.</summary>
    public int SourceStart => Source.Length > 0 ? Source[0] : 0;
}

/// <summary>
/// Turns a page's text (<see cref="PageText.Text"/>: lines in reading order, joined by '\n') into paragraphs for speech.
/// Lines are joined with a space, a word hyphenated across a line break is put back together, and a paragraph ends at
/// a blank line, a bullet or numbered item, or a sentence that stops short of the line width. Long paragraphs are cut at
/// sentence ends so each one is synthesized quickly.
/// </summary>
public static class ReadAloudText
{
    /// <param name="from">First page-text index to read (e.g. the selection's start).</param>
    /// <param name="to">End of the text to read (exclusive).</param>
    /// <param name="maxLength">Longest chunk; longer paragraphs are split at sentence ends.</param>
    public static List<SpeechChunk> Chunks(int page, string pageText, int from = 0, int to = int.MaxValue, int maxLength = 1200)
    {
        from = Math.Clamp(from, 0, pageText.Length);
        to = Math.Clamp(to, from, pageText.Length);
        var lines = new List<(int Start, int End)>();
        for (var s = from; s < to;)
        {
            var nl = pageText.IndexOf('\n', s, to - s);
            var e = nl < 0 ? to : nl;
            lines.Add((s, e));
            s = e + 1;
        }
        var typical = Typical(lines.Select(l => pageText.AsSpan(l.Start, l.End - l.Start).Trim().Length).Where(n => n > 0).ToList());

        var chunks = new List<SpeechChunk>();
        var sb = new StringBuilder();
        var src = new List<int>();
        void Flush()
        {
            // Trim, keeping the mapping in step.
            var a = 0;
            var b = sb.Length;
            while (a < b && char.IsWhiteSpace(sb[a])) a++;
            while (b > a && char.IsWhiteSpace(sb[b - 1])) b--;
            if (b > a && sb.ToString(a, b - a).Any(char.IsLetterOrDigit))
                foreach (var (s, e) in SplitLong(sb.ToString(a, b - a), maxLength))
                    chunks.Add(new SpeechChunk(page, sb.ToString(a + s, e - s), [.. src.GetRange(a + s, e - s)]));
            sb.Clear();
            src.Clear();
        }

        string? prev = null;
        foreach (var (start, end) in lines)
        {
            var line = pageText[start..end];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                prev = null;
                continue;
            }
            if (prev is not null && StartsNewParagraph(prev, trimmed, typical)) Flush();

            var lead = line.Length - line.TrimStart().Length;
            var body = line.TrimEnd();
            if (sb.Length > 0)
            {
                if (EndsHyphenated(sb) && trimmed.Length > 0 && char.IsLower(trimmed[0]))
                {
                    // "exam-" + "ple" → "example": drop the hyphen.
                    sb.Length--;
                    src.RemoveAt(src.Count - 1);
                }
                else
                {
                    sb.Append(' ');
                    src.Add(start - 1 >= 0 ? start - 1 : start); // the line break
                }
            }
            for (var i = lead; i < body.Length; i++)
            {
                var c = body[i];
                // Collapse runs of spaces (text extraction leaves them between columns of words).
                if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] == ' ') continue;
                sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
                src.Add(start + i);
            }
            prev = trimmed;
        }
        Flush();
        return chunks;
    }

    /// <summary>The length a full line has on this page (the longer lines' median), 0 when unknown.</summary>
    private static int Typical(List<int> lengths)
    {
        if (lengths.Count < 3) return 0;
        lengths.Sort();
        return lengths[(int)(lengths.Count * 0.75)];
    }

    private static bool EndsHyphenated(StringBuilder sb) =>
        sb.Length >= 2 && sb[^1] is '-' or '­' && char.IsLetter(sb[^2]);

    private static bool StartsNewParagraph(string prev, string line, int typical)
    {
        if (IsListItem(line)) return true;
        var last = prev[^1];
        var ended = last is '.' or '!' or '?' or ':' or '"' or '”' or ')' or '…';
        // A sentence that stops well short of the usual line width closes its paragraph.
        if (ended && typical > 0 && prev.Length < typical * 0.8) return true;
        // A heading: short, no closing punctuation, followed by a capitalized line.
        return typical > 0 && prev.Length < typical * 0.5 && !ended && last is not (',' or '-' or ';') && char.IsUpper(line[0]);
    }

    private static bool IsListItem(string line)
    {
        if (line[0] is '•' or '●' or '▪' or '–' or '—' or '*' or '◦') return true;
        if (line.Length >= 2 && line[0] == '-' && line[1] == ' ') return true;
        var digits = 0;
        while (digits < line.Length && digits < 3 && char.IsDigit(line[digits])) digits++;
        return digits > 0 && digits < line.Length - 1 && line[digits] is '.' or ')' && line[digits + 1] == ' ';
    }

    /// <summary>Pieces of at most <paramref name="max"/> characters, cut after a sentence end (else a space) where possible.</summary>
    private static IEnumerable<(int Start, int End)> SplitLong(string text, int max)
    {
        var start = 0;
        while (text.Length - start > max)
        {
            var limit = start + max;
            var cut = -1;
            for (var i = limit - 1; i > start + max / 3; i--)
                if (text[i] == ' ' && text[i - 1] is '.' or '!' or '?' or ';')
                {
                    cut = i;
                    break;
                }
            if (cut < 0) cut = text.LastIndexOf(' ', limit - 1, limit - start - 1);
            if (cut <= start) cut = limit;
            yield return (start, cut);
            start = cut;
            while (start < text.Length && text[start] == ' ') start++;
        }
        if (start < text.Length) yield return (start, text.Length);
    }
}
