using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>
/// Form actions in a tab: push buttons and field triggers run their actions (one at a time, after pending field values
/// are written), live keystroke checks, and the host that carries out what leaves the document, each step only after
/// the user agreed (per action, or "always" for this tab's session).
/// </summary>
public partial class DocumentTab
{
    private FormActionHost? _formHost;
    private readonly Queue<FormWidgetAction> _actionQueue = new();
    private bool _actionsRunning;

    private IFormActionHost FormHost => _formHost ??= new FormActionHost(this);

    private void InitFormActions()
    {
        Viewer.WidgetAction += (_, e) => QueueWidgetAction(e);
        Viewer.Keystrokes = new KeystrokeBridge(this);
    }

    private void QueueWidgetAction(FormWidgetAction e)
    {
        if (_disposed || !Session.IsPdf) return;
        // Hover actions may be dropped while others run; clicks and focus changes are kept (within reason).
        if (e.Trigger is FormTrigger.Enter or FormTrigger.Exit && (_actionsRunning || _actionQueue.Count > 0)) return;
        if (_actionQueue.Count >= 16) return;
        _actionQueue.Enqueue(e);
        if (!_actionsRunning) _ = RunWidgetActions();
    }

    private async Task RunWidgetActions()
    {
        _actionsRunning = true;
        try
        {
            while (_actionQueue.Count > 0 && !_disposed)
            {
                // A value typed or clicked just before must be in the document first: the scripts read it.
                while (!_disposed && (_pendingWrites > 0 || IsBusy))
                    await (_pendingWrites > 0 ? WhenWritesDone() : WhenIdle());
                if (_disposed) break;
                await RunWidgetAction(_actionQueue.Dequeue());
            }
        }
        finally
        {
            _actionsRunning = false;
        }
    }

    private async Task RunWidgetAction(FormWidgetAction e)
    {
        BeginWrite();
        try
        {
            var data = Session.Data;
            var password = Session.Password;
            var w = e.Widget;
            var host = FormHost;
            FormActionResult r;
            try
            {
                r = await Task.Run(() => FormEditor.RunAction(data, password, w.FieldName, w.PageIndex, w.AnnotIndex, e.Trigger, host));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"Form action ({e.Trigger} on {w.FieldName}) failed: {ex.Message}");
                return;
            }
            if (_disposed) return;
            foreach (var line in r.Log) Log.Write(line);
            foreach (var alert in r.Alerts.Distinct().Take(3)) Notify(alert);
            if (r.NeedsPermission)
                Notify("This form is protected: its buttons can't change fields until the permissions password is entered (fill a field to be asked for it).");
            if (r.Data is { } updated)
            {
                while (!_disposed)
                {
                    if (!ReferenceEquals(Session.Data, data))
                    {
                        Notify("The document changed while the form's action ran, so its changes weren't kept. Try again.", error: true);
                        break;
                    }
                    var outcome = await TryEdit(r.Label, (d, _) => ReferenceEquals(d, data) ? updated : d,
                        quiet: r.Label is not ("Reset form" or "Import form data"), contentOnly: true);
                    if (outcome != EditOutcome.Busy) break;
                    await WhenIdle();
                }
            }
            if (r.FocusField is { } focus && !_disposed) Viewer.FocusField(focus);
        }
        finally
        {
            EndWrite();
        }
    }

    // ───────────────────────────── Live keystrokes ─────────────────────────────

    /// <summary>Opens the field's keystroke script off the UI thread when its editor opens; checks edits once it's ready.</summary>
    private sealed class KeystrokeBridge(DocumentTab tab) : IFieldKeystrokes
    {
        private Task<FieldKeystrokes?>? _open;
        private string? _field;

        public void Prepare(FormWidget widget)
        {
            Release();
            if (!tab.Session.IsPdf) return;
            var data = tab.Session.Data;
            var password = tab.Session.Password;
            var name = widget.FieldName;
            _field = name;
            _open = Task.Run(() =>
            {
                try
                {
                    return FieldKeystrokes.Open(data, password, name);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Write($"Keystroke script of {name} unavailable: {ex.Message}");
                    return null;
                }
            });
        }

        public KeystrokeResult? Filter(FormWidget widget, string value, string change, int selStart, int selEnd)
        {
            if (widget.FieldName != _field || _open is not { IsCompletedSuccessfully: true } t || t.Result is not { } keys) return null;
            return keys.Run(value, change, selStart, selEnd);
        }

        public void Release()
        {
            var open = _open;
            _open = null;
            _field = null;
            open?.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) t.Result?.Dispose();
            }, TaskScheduler.Default);
        }
    }

    // ───────────────────────────── The host ─────────────────────────────

    /// <summary>
    /// Carries out form actions for this tab. Calls arrive on a background thread; UI work is marshalled to the tab's
    /// dispatcher. Nothing leaves the document without a consent dialog naming the destination ("Always allow" lasts
    /// for this tab's session, per destination and per kind of data sent: see <see cref="FormConsent"/>); programs are
    /// never started and network shares never opened. Each method reports whether the step went ahead, so a declined
    /// one stops the script that asked.
    /// </summary>
    private sealed class FormActionHost(DocumentTab tab) : IFormActionHost
    {
        private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        /// <summary>Files a Launch action may open with their default app: documents and media, never programs or scripts.</summary>
        private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".txt", ".rtf", ".csv", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".mp3", ".mp4", ".wav",
        };

        private readonly HashSet<string> _always = new(StringComparer.Ordinal); // UI thread only

        private T Ui<T>(Func<T> run) => tab.Dispatcher.CheckAccess() ? run() : tab.Dispatcher.Invoke(run);

        private void Post(Action run) => tab.Dispatcher.BeginInvoke(() =>
        {
            if (!tab._disposed) run();
        });

        private void Toast(string message, bool error = false) => Post(() => tab.Notify(message, error: error));

        private Window? Owner => Window.GetWindow(tab);

        /// <summary>Asks (unless "Always allow" was chosen for this <paramref name="key"/>: destination and what is sent).</summary>
        private bool Consent(string kind, string title, string body, string destination, string? key = null) => Ui(() =>
        {
            if (tab._disposed) return false;
            key ??= kind + "\n" + destination;
            if (_always.Contains(key)) return true;
            var choice = FormActionDialogs.Consent(Owner, title, body, destination);
            if (choice == ConsentChoice.Always) _always.Add(key);
            Log.Write($"Form action consent ({kind}): {choice}");
            return choice != ConsentChoice.Cancel;
        });

        public int CurrentPage => Ui(() => tab.Viewer.CurrentPage);

        public void GoToPage(int page, double? y) => Ui(() =>
        {
            if (!tab._disposed && page >= 0 && page < tab.Viewer.PageCount) tab.Viewer.GoToPage(page, y);
            return 0;
        });

        public bool OpenUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto"))
            {
                Toast($"The form tried to open {Short(url)}. Only web and email links can be opened from a form.", error: true);
                return false;
            }
            var mail = u.Scheme == "mailto";
            if (!Consent("url", mail ? "Write an email?" : "Open a web page?",
                    mail ? "The form wants to start an email in your email app, to:" : "The form wants to open this address in your browser:", u.OriginalString))
                return false;
            Post(() => Shell.Open(u.AbsoluteUri));
            return true;
        }

        public Task<int>? Submit(FormSubmission s)
        {
            if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            {
                Toast($"The form tried to send its data to {Short(s.Url)}. Form data can only be sent to web addresses (http or https).", error: true);
                return null;
            }
            var what = s.Format switch
            {
                SubmitFormat.Pdf => $"the whole document ({Size(s.Body.Length)}), including the values of its {s.FieldCount} field{Plural(s.FieldCount)},",
                SubmitFormat.Fdf => $"the values of {s.FieldCount} field{Plural(s.FieldCount)} (FDF, {Size(s.Body.Length)})",
                SubmitFormat.Xfdf => $"the values of {s.FieldCount} field{Plural(s.FieldCount)} (XFDF, {Size(s.Body.Length)})",
                _ => $"the values of {s.FieldCount} field{Plural(s.FieldCount)} (web form{(s.Method == "GET" ? ", in the address" : $", {Size(s.Body.Length)}")})",
            };
            var destination = $"{s.Method} {u.GetLeftPart(UriPartial.Path)}";
            if (!Consent("submit", "Send form data?", $"The form wants to send {what} to this address. Nothing is sent unless you allow it.", destination,
                    FormConsent.SubmitKey(s)))
                return null;
            return Send(u, s);
        }

        private async Task<int> Send(Uri u, FormSubmission s)
        {
            try
            {
                using var request = new HttpRequestMessage(s.Method == "GET" ? HttpMethod.Get : HttpMethod.Post, u);
                if (s.Method != "GET")
                {
                    request.Content = new ByteArrayContent(s.Body);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(s.ContentType);
                }
                request.Headers.UserAgent.ParseAdd("InkvoltPdf");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                Log.Write($"Form data sent to {u.Host}: HTTP {code}");
                Toast(response.IsSuccessStatusCode ? $"Form sent to {u.Host} ({code} {response.ReasonPhrase})." : $"{u.Host} didn't accept the form ({code} {response.ReasonPhrase}).",
                    error: !response.IsSuccessStatusCode);
                return code;
            }
            catch (TaskCanceledException)
            {
                Toast($"Sending the form to {u.Host} timed out.", error: true);
                return 0;
            }
            catch (HttpRequestException ex)
            {
                Log.Write($"Form data to {u.Host} not sent: {ex.Message}");
                Toast($"Couldn't send the form to {u.Host}: {ex.Message}", error: true);
                return 0;
            }
        }

        public bool Mail(FormMail m)
        {
            var attachment = m.Attachment is null ? "" : $" The {(m.AttachmentName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true ? "document" : "form data")} " +
                $"({Size(m.Attachment.Length)}) is saved to a temporary folder for you to attach.";
            var to = m.To.Length > 0 ? m.To : "(you choose)";
            var copies = string.Join("", new[] { ("cc", m.Cc), ("bcc", m.Bcc) }.Where(x => x.Item2.Length > 0).Select(x => $"\n{x.Item1}: {Short(x.Item2)}"));
            if (!Consent("mail", "Write an email?", $"The form wants to start an email in your email app.{attachment}", $"mailto:{Short(to)}{copies}", FormConsent.MailKey(m)))
                return false;
            string? path = null;
            if (m.Attachment is not null)
            {
                try
                {
                    var name = m.AttachmentName == "form.pdf" ? Path.ChangeExtension(SafeName(tab.Session.DisplayName), ".pdf") : SafeName(m.AttachmentName ?? "form.fdf");
                    var dir = Path.Combine(Path.GetTempPath(), "InkvoltPdf", "Mail", Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, name);
                    File.WriteAllBytes(path, m.Attachment);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Toast($"Couldn't save the attachment: {ex.Message}", error: true);
                    return false;
                }
            }
            var parts = new List<string>();
            void Part(string key, string value)
            {
                if (value.Length > 0) parts.Add(key + "=" + Uri.EscapeDataString(value));
            }
            Part("cc", m.Cc);
            Part("bcc", m.Bcc);
            Part("subject", m.Subject);
            var body = m.Message.Length > 1500 ? m.Message[..1500] : m.Message;
            if (path is not null) body += (body.Length > 0 ? "\n\n" : "") + "Attach this file: " + path;
            Part("body", body);
            var address = string.Join(',', m.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Uri.EscapeDataString));
            var mailto = "mailto:" + address + (parts.Count > 0 ? "?" + string.Join('&', parts) : "");
            Post(() =>
            {
                Shell.Open(mailto);
                if (path is not null)
                {
                    Shell.ShowInFolder(path);
                    tab.Notify($"Attach {Path.GetFileName(path)} to the email (its folder is open).");
                }
            });
            return true;
        }

        public void Print(int? fromPage, int? toPage) => Post(() => Cmd.Print.Execute(null, tab.Viewer));

        public int Alert(FormAlert alert) => Ui(() => tab._disposed ? 1 : FormActionDialogs.Alert(Owner, alert));

        public string? Response(FormPrompt prompt) => Ui(() => tab._disposed ? null : FormActionDialogs.Response(Owner, prompt));

        public void Named(string name) => Post(() =>
        {
            switch (name)
            {
                case "Print": Cmd.Print.Execute(null, tab.Viewer); break;
                case "GoBack": tab.Viewer.GoBack(); break;
                case "GoForward": tab.Viewer.GoForward(); break;
                case "Find": Cmd.Find.Execute(null, tab.Viewer); break;
                case "FullScreen": Cmd.FullScreen.Execute(null, tab.Viewer); break;
            }
        });

        public bool OpenRemote(string path, int page)
        {
            if (Resolve(path) is not { } full || !Path.GetExtension(full).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                Toast($"The form tried to open {Short(path)}, which isn't a PDF on this computer.", error: true);
                return false;
            }
            if (!File.Exists(full))
            {
                Toast($"The form tried to open {Path.GetFileName(full)}, which can't be found.", error: true);
                return false;
            }
            if (!Consent("file", "Open another PDF?", $"The form wants to open this PDF in a new tab{(page > 0 ? $", at page {page + 1}" : "")}:", full)) return false;
            Post(() => _ = OpenPdf(full, page));
            return true;
        }

        private async Task OpenPdf(string path, int page)
        {
            if (Window.GetWindow(tab) is not MainWindow main) return;
            await main.OpenFileAsync(path);
            if (page > 0 && main.ActiveTab is { } t && string.Equals(t.Session.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && page < t.PageView.PageCount)
                t.PageView.GoToPage(page);
        }

        public bool Launch(string path)
        {
            if (Resolve(path) is not { } full)
            {
                Toast($"The form tried to open {Short(path)}, which isn't a file on this computer.", error: true);
                return false;
            }
            var ext = Path.GetExtension(full);
            if (!DocumentTypes.Contains(ext))
            {
                Log.Write($"Form Launch action refused: {Path.GetFileName(full)}");
                Toast($"The form tried to start {Path.GetFileName(full)}. Inkvolt PDF never runs programs or scripts from a document.", error: true);
                return false;
            }
            if (!File.Exists(full))
            {
                Toast($"The form tried to open {Path.GetFileName(full)}, which can't be found.", error: true);
                return false;
            }
            var pdf = ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!Consent("file", "Open a file?", pdf ? "The form wants to open this PDF in a new tab:" : "The form wants to open this file with its default app:", full)) return false;
            Post(() =>
            {
                if (pdf) _ = OpenPdf(full, 0);
                else Shell.Open(full);
            });
            return true;
        }

        public byte[]? ImportData(string? suggestedPath)
        {
            var suggested = suggestedPath is null ? null : Resolve(suggestedPath);
            if (!Consent("import", "Fill the form from a data file?",
                    "The form wants to fill its fields from form data (FDF or XFDF). You pick the file next; its values replace the fields' values.",
                    suggested ?? "(you choose the file)"))
                return null;
            return Ui(() =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Import form data",
                    Filter = "Form data (*.fdf;*.xfdf)|*.fdf;*.xfdf|All files (*.*)|*.*",
                    CheckFileExists = true,
                };
                if (suggested is not null)
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(suggested);
                    dlg.FileName = Path.GetFileName(suggested);
                }
                if (dlg.ShowDialog(Owner) != true) return null;
                try
                {
                    var info = new FileInfo(dlg.FileName);
                    if (info.Length > 16 * 1024 * 1024)
                    {
                        tab.Notify("That file is too large to be form data.", error: true);
                        return null;
                    }
                    return File.ReadAllBytes(dlg.FileName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    tab.Notify($"Couldn't read {Path.GetFileName(dlg.FileName)}: {ex.Message}", error: true);
                    return null;
                }
            });
        }

        public void Beep(int type) => MessageBeep(type switch { 0 => 0x10u, 1 => 0x30u, 2 => 0x20u, 3 => 0x40u, _ => 0u });

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MessageBeep(uint type);

        /// <summary>
        /// A file specification as a local path (relative ones from this document's folder; PDF-style /C/dir/file too),
        /// or null: URLs, network shares and device paths are refused (opening a share could leak the user's credentials).
        /// </summary>
        private string? Resolve(string spec)
        {
            spec = spec.Trim();
            if (spec.Length == 0) return null;
            string path;
            if (Regex.IsMatch(spec, "^/[A-Za-z]/")) path = $"{spec[1]}:{spec[2..]}".Replace('/', '\\');
            else if (Uri.TryCreate(spec, UriKind.Absolute, out var u) && u.Scheme.Length > 1)
            {
                if (!u.IsFile || u.IsUnc) return null;
                path = u.LocalPath;
            }
            else path = spec.Replace('/', '\\');
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            if (!Path.IsPathRooted(path))
            {
                if (Path.GetDirectoryName(tab.Session.FilePath) is not { Length: > 0 } folder) return null;
                path = Path.Combine(folder, path);
            }
            try
            {
                var full = Path.GetFullPath(path);
                return full.StartsWith(@"\\", StringComparison.Ordinal) ? null : full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        private static string SafeName(string name)
        {
            var clean = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return clean.Length == 0 ? "form" : clean;
        }

        private static string Short(string s) => s.Length > 120 ? s[..120] + "…" : s;

        private static string Plural(int n) => n == 1 ? "" : "s";

        private static string Size(long bytes) => bytes < 1024 ? $"{bytes} bytes" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1048576.0:0.#} MB";
    }
}
