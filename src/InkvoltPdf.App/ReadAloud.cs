using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace InkvoltPdf.App;

/// <summary>
/// Read Aloud (View › Read Aloud, Ctrl+Shift+Y): reads the document from the page in view (or the selection) with a
/// Windows voice, paragraph by paragraph, highlighting the sentence and word being read and scrolling along. A floating
/// bar at the top of the page view has play/pause, stop, previous/next paragraph, the voice and the speed. One per tab;
/// it stops when the document changes or the tab closes.
/// </summary>
public sealed class ReadAloud : IDisposable
{
    private static readonly double[] Speeds = [0.75, 1, 1.25, 1.5, 1.75, 2];
    /// <summary>The voice and speed chosen last (this session), used by every tab.</summary>
    private static string? _voiceId;
    private static double _speed = 1;

    private readonly DocumentTab _tab;
    private readonly PageViewer _viewer;
    private readonly Grid _host;
    private readonly Dispatcher _dispatcher;
    private readonly SpeechSynthesizer _synth = new();
    private readonly Windows.Media.Playback.MediaPlayer _player = new() { AutoPlay = false };
    private readonly DispatcherTimer _tick;
    private readonly Border _bar;
    private readonly Button _playButton;
    private readonly ComboBox _voices;
    private readonly ComboBox _speeds;
    private readonly TextBlock _status;

    private PdfEngine? _engine;
    private PageText? _pageText;
    private List<SpeechChunk> _chunks = [];
    private int _chunkIndex;
    private int _page;
    /// <summary>Reading a selection: where it ends (nothing after it is read).</summary>
    private TextPos? _end;
    private SpeechChunk? _speaking;
    private List<(TimeSpan Start, TimeSpan End, int From, int To)> _words = [];
    private List<(TimeSpan Start, TimeSpan End, int From, int To)> _sentences = [];
    private int _run; // bumps on every start/stop, so a stale synthesis or end-of-media is ignored
    private bool _paused;
    private int _lastWord = -1;

    public bool IsActive { get; private set; }

    public ReadAloud(DocumentTab tab, PageViewer viewer, Grid host)
    {
        _tab = tab;
        _viewer = viewer;
        _host = host;
        _dispatcher = viewer.Dispatcher;
        _tick = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        _tick.Tick += (_, _) => FollowPosition();
        _player.MediaEnded += (_, _) => _dispatcher.BeginInvoke(() => OnChunkEnded(_run));
        _player.MediaFailed += (_, e) => _dispatcher.BeginInvoke(() => Fail($"The voice couldn't be played: {e.ErrorMessage}"));

        _synth.Options.IncludeWordBoundaryMetadata = true;
        _synth.Options.IncludeSentenceBoundaryMetadata = true;

        Button Icon(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, ToolTip = tip, FontSize = 14 };
            b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        _playButton = Icon("", "Pause", TogglePause);
        _voices = new ComboBox { Width = 210, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Voice" };
        System.Windows.Automation.AutomationProperties.SetName(_voices, "Voice");
        _speeds = new ComboBox { Width = 76, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Speed" };
        System.Windows.Automation.AutomationProperties.SetName(_speeds, "Speed");
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), MinWidth = 52, FontSize = 12 };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var voices = SpeechSynthesizer.AllVoices.OrderBy(v => v.Language != System.Globalization.CultureInfo.CurrentUICulture.Name).ThenBy(v => v.DisplayName).ToList();
        _voices.ItemsSource = voices.Select(v => $"{v.DisplayName} ({v.Language})").ToList();
        var voice = voices.FirstOrDefault(v => v.Id == _voiceId) ?? voices.FirstOrDefault(v => v.Id == SpeechSynthesizer.DefaultVoice?.Id) ?? voices.FirstOrDefault();
        if (voice is not null)
        {
            _synth.Voice = voice;
            _voices.SelectedIndex = voices.IndexOf(voice);
        }
        _voices.SelectionChanged += (_, _) =>
        {
            if (_voices.SelectedIndex < 0) return;
            _synth.Voice = voices[_voices.SelectedIndex];
            _voiceId = _synth.Voice.Id;
            RestartChunk();
        };
        _speeds.ItemsSource = Speeds.Select(s => $"{s:0.##}×").ToList();
        _speeds.SelectedIndex = Math.Max(0, Array.IndexOf(Speeds, _speed));
        _synth.Options.SpeakingRate = _speed;
        _speeds.SelectionChanged += (_, _) =>
        {
            if (_speeds.SelectedIndex < 0) return;
            _speed = Speeds[_speeds.SelectedIndex];
            _synth.Options.SpeakingRate = _speed;
            RestartChunk();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = "", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        title.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        row.Children.Add(title);
        row.Children.Add(Icon("", "Previous paragraph", () => Skip(-1)));
        row.Children.Add(_playButton);
        row.Children.Add(Icon("", "Next paragraph", () => Skip(1)));
        row.Children.Add(Icon("", $"Stop ({Cmd.GestureText(Cmd.ReadAloud)})", Stop));
        row.Children.Add(_status);
        var sep = new Border();
        sep.SetResourceReference(FrameworkElement.StyleProperty, "BarSeparator");
        row.Children.Add(sep);
        row.Children.Add(_voices);
        row.Children.Add(_speeds);
        _bar = new Border
        {
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.22 },
        };
        _bar.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        _bar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        System.Windows.Automation.AutomationProperties.SetName(_bar, "Read aloud");
        Grid.SetRow(_bar, 1);
        _host.Children.Add(_bar);
    }

    /// <summary>The bar, for snapshots and tests.</summary>
    internal FrameworkElement Bar => _bar;

    internal string State => !IsActive ? "stopped" : $"{(_paused ? "paused" : "reading")} page {_page + 1} paragraph {_chunkIndex + 1}/{_chunks.Count}" +
        $" voice='{_synth.Voice?.DisplayName}' speed={_speed} '{Trunc(_speaking?.Text)}'";

    /// <summary>Dev script: the previous/next paragraph buttons.</summary>
    internal void DevSkip(int direction) => Skip(direction);

    private static string Trunc(string? s) => s is null ? "" : s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>Starts reading from the selection, else from the top of the view; reading already: stops.</summary>
    public async Task Toggle()
    {
        if (IsActive)
        {
            Stop();
            return;
        }
        if (SpeechSynthesizer.AllVoices.Count == 0)
        {
            Prompt.Info(Window.GetWindow(_tab), "No voices installed", "Add a voice in Windows Settings › Time & language › Speech, then try again.");
            return;
        }
        var run = ++_run;
        IsActive = true;
        _paused = false;
        _engine = _tab.Session.Engine;
        _bar.Visibility = Visibility.Visible;
        UpdateButtons();

        int page, from;
        if (_viewer.SelectionBounds is { } sel)
        {
            (page, from, _end) = (sel.From.Page, sel.From.Caret, sel.To);
        }
        else
        {
            var (p, top) = _viewer.ViewTarget();
            page = p;
            from = 0;
            _end = null;
            if (top is { } y && await TextOf(page, run) is { } t && run == _run)
            {
                var caret = t.HitTest(36, y + 4);
                from = t.LineAt(caret).Start;
            }
        }
        if (run != _run) return;
        await LoadPage(page, from, run);
        if (run != _run) return;
        _chunkIndex = 0;
        await SpeakNextAvailable(run);
    }

    public void Stop()
    {
        _run++;
        IsActive = false;
        _paused = false;
        _tick.Stop();
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        _speaking = null;
        _engine = null;
        _viewer.ClearSpeechHighlight();
        _bar.Visibility = Visibility.Collapsed;
        _tab.RaiseReadAloudChanged();
    }

    private void TogglePause()
    {
        if (!IsActive) return;
        if (_paused)
        {
            _paused = false;
            _player.Play();
            _tick.Start();
        }
        else
        {
            _paused = true;
            _player.Pause();
            _tick.Stop();
        }
        UpdateButtons();
    }

    private void Skip(int direction)
    {
        if (!IsActive) return;
        var run = ++_run;
        _paused = false;
        _player.Pause();
        _ = SkipAsync(direction, run);
    }

    private async Task SkipAsync(int direction, int run)
    {
        var index = _chunkIndex + direction;
        if (index < 0)
        {
            // Back to the last paragraph of the page before.
            for (var p = _page - 1; p >= 0; p--)
            {
                await LoadPage(p, 0, run);
                if (run != _run) return;
                if (_chunks.Count > 0)
                {
                    _chunkIndex = _chunks.Count - 1;
                    await Speak(_chunks[_chunkIndex], run);
                    return;
                }
            }
            index = 0;
            await LoadPage(0, 0, run);
        }
        _chunkIndex = index;
        await SpeakNextAvailable(run);
    }

    /// <summary>Speaks the current chunk, moving on to the following pages when this one has nothing (more) to read.</summary>
    private async Task SpeakNextAvailable(int run)
    {
        while (run == _run && _chunkIndex >= _chunks.Count)
        {
            if (_end is { } end && _page >= end.Page || _engine is null || _page + 1 >= _engine.PageCount)
            {
                Stop();
                return;
            }
            await LoadPage(_page + 1, 0, run);
            _chunkIndex = 0;
        }
        if (run != _run) return;
        await Speak(_chunks[_chunkIndex], run);
    }

    private async Task<PageText?> TextOf(int page, int run)
    {
        var engine = _engine;
        if (engine is null) return null;
        try
        {
            return await Task.Run(() => engine.GetText(page));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or MuPDFCore.MuPDFException)
        {
            if (run == _run) Stop();
            return null;
        }
    }

    private async Task LoadPage(int page, int from, int run)
    {
        var text = await TextOf(page, run);
        if (run != _run || text is null) return;
        _page = page;
        _pageText = text;
        var to = _end is { } end && end.Page == page ? end.Caret : int.MaxValue;
        _chunks = ReadAloudText.Chunks(page, text.Text, from, to);
    }

    private async Task Speak(SpeechChunk chunk, int run)
    {
        _speaking = chunk;
        _lastWord = -1;
        UpdateButtons();
        SpeechSynthesisStream stream;
        try
        {
            stream = await _synth.SynthesizeTextToStreamAsync(chunk.Text);
        }
        catch (Exception ex)
        {
            Log.Write($"Read aloud: synthesis failed: {ex}");
            if (run == _run) Fail("The voice couldn't read this text.");
            return;
        }
        if (run != _run)
        {
            stream.Dispose();
            return;
        }
        _words = Cues(stream, "Word");
        _sentences = Cues(stream, "Sentence");
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        if (!_paused) _player.Play();
        _tick.Start();
        // Show where reading starts right away (the first word's cue may come a moment later).
        Highlight(0, Math.Min(chunk.Text.Length, chunk.Text.IndexOf(' ') is > 0 and var sp ? sp : chunk.Text.Length), 0, chunk.Text.Length);
    }

    /// <summary>A track's cues: time span and spoken-text range, in order.</summary>
    private static List<(TimeSpan Start, TimeSpan End, int From, int To)> Cues(SpeechSynthesisStream stream, string kind)
    {
        var track = stream.TimedMetadataTracks.FirstOrDefault(t => (t.Id ?? "").Contains(kind, StringComparison.OrdinalIgnoreCase)
                                                                   || (t.Label ?? "").Contains(kind, StringComparison.OrdinalIgnoreCase));
        if (track is null) return [];
        return [.. track.Cues.OfType<SpeechCue>()
            .Where(c => c.StartPositionInInput is not null && c.EndPositionInInput is not null)
            .Select(c => (c.StartTime, c.StartTime + c.Duration, c.StartPositionInInput!.Value, c.EndPositionInInput!.Value + 1))
            .OrderBy(c => c.StartTime)];
    }

    private void FollowPosition()
    {
        if (!IsActive || _paused || _speaking is null) return;
        TimeSpan now;
        try
        {
            now = _player.PlaybackSession.Position;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return;
        }
        var w = LastStartedBy(_words, now);
        if (w < 0 || w == _lastWord) return;
        _lastWord = w;
        var word = _words[w];
        var s = LastStartedBy(_sentences, now);
        var (sFrom, sTo) = s >= 0 ? (_sentences[s].From, _sentences[s].To) : (word.From, word.To);
        Highlight(word.From, word.To, sFrom, sTo);
    }

    private static int LastStartedBy(List<(TimeSpan Start, TimeSpan End, int From, int To)> cues, TimeSpan t)
    {
        int lo = 0, hi = cues.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cues[mid].Start <= t)
            {
                found = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return found;
    }

    private void Highlight(int wordFrom, int wordTo, int sentenceFrom, int sentenceTo)
    {
        if (_speaking is not { } chunk || _pageText is not { } text || chunk.Text.Length == 0) return;
        var (ws, we) = chunk.SourceRange(wordFrom, wordTo);
        var (ss, se) = chunk.SourceRange(sentenceFrom, sentenceTo);
        var word = text.GetRects(ws, Math.Min(we, text.Text.Length));
        _viewer.SetSpeechHighlight(chunk.Page, text.GetRects(ss, Math.Min(se, text.Text.Length)), word);
        if (word.Count > 0) _viewer.FollowSpeech(chunk.Page, word.Aggregate((a, b) => a.Union(b)));
    }

    private void OnChunkEnded(int run)
    {
        if (run != _run || !IsActive) return;
        _tick.Stop();
        _chunkIndex++;
        _ = SpeakNextAvailable(run);
    }

    /// <summary>Synthesizes the current paragraph again from the sentence being read (new voice or speed).</summary>
    private void RestartChunk()
    {
        if (!IsActive || _speaking is not { } chunk) return;
        var run = ++_run;
        var at = 0;
        try
        {
            var s = LastStartedBy(_sentences, _player.PlaybackSession.Position);
            if (s >= 0) at = Math.Clamp(_sentences[s].From, 0, chunk.Text.Length - 1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        _player.Pause();
        var rest = at == 0 ? chunk : new SpeechChunk(chunk.Page, chunk.Text[at..], chunk.Source[at..]);
        _chunks[_chunkIndex] = rest;
        _ = Speak(rest, run);
    }

    private void Fail(string message)
    {
        Stop();
        _tab.ShowNotice(message, error: true);
    }

    private void UpdateButtons()
    {
        _playButton.Content = _paused ? "" : "";
        var tip = _paused ? "Resume" : "Pause";
        _playButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(_playButton, tip);
        _status.Text = IsActive ? $"Page {_page + 1}" : "";
        _tab.RaiseReadAloudChanged();
    }

    public void Dispose()
    {
        Stop();
        _host.Children.Remove(_bar);
        _player.Dispose();
        _synth.Dispose();
    }
}
