using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>Sign Digitally (dialog → box → sign → Save As) and the Sign menu of saved handwritten signatures.</summary>
public partial class MainWindow
{
    private bool _signing;

    /// <summary>
    /// Signs the active document: commits open editors and waits for pending writes, asks for the digital ID and options,
    /// lets the user drag the box (visible, new field), signs as an incremental update, then saves the signed copy
    /// (default "&lt;name&gt;-signed.pdf"); the tab then shows the signed file and its verification.
    /// </summary>
    internal async Task SignDigitallyAsync(DocumentTab t, string? field, SignChoice? scripted = null, SignaturePlacement? scriptedBox = null)
    {
        if (_signing) return;
        _signing = true;
        try
        {
            await t.FlushPendingEditsAsync();
            if (t.IsDisposed) return;
            if (t.Session.Engine.IsEncrypted)
            {
                Prompt.Info(this, "Password-protected PDFs can't be signed",
                    "Save a copy without the password protection first (Save As), then sign that copy.");
                return;
            }
            var (aspect, fieldVisible) = field is null ? (3.6, true) : t.SignatureFieldShape(field);
            var choice = scripted ?? SignDialog.Show(this, field, aspect, fieldVisible, t.IsSigned);
            if (choice is null) return;
            using var cert = choice.Certificate;

            SignaturePlacement? box = null;
            if (field is null && choice.Visible)
            {
                box = scriptedBox ?? await t.PickSignatureBoxAsync();
                if (box is null || t.IsDisposed)
                {
                    ShowStatus("Signing cancelled");
                    return;
                }
            }
            ShowStatus(choice.Timestamp ? "Signing and getting a timestamp…" : "Signing…");
            var request = new SignRequest
            {
                Certificate = cert,
                FieldName = field,
                PageIndex = box?.PageIndex ?? 0,
                Rect = box?.Rect,
                Reason = choice.Reason,
                Location = choice.Location,
                Look = choice.Look,
                Timestamp = choice.Timestamp ? DocumentTab.TimestampClient(choice.TimestampUrl) : null,
                Certify = choice.Certify,
            };
            var outcome = await t.ApplySignatureAsync(request);
            if (outcome == EditOutcome.Declined) ShowStatus("Signing cancelled"); // e.g. the PIN prompt was cancelled
            if (outcome != EditOutcome.Done || t.IsDisposed) return;

            // The signature must be the file's last change: save the signed bytes as they are.
            var name = Path.GetFileNameWithoutExtension(t.Session.DisplayName);
            if (!await SaveAsDialogAsync(t, name.EndsWith("-signed", StringComparison.OrdinalIgnoreCase) ? name + ".pdf" : name + "-signed.pdf"))
            {
                if (!t.IsDisposed && t.Session.UndoLabel == "Sign") t.Undo();
                ShowStatus("Not signed: the signed copy wasn't saved");
                return;
            }
            t.ShowSignatureDetails(field);
        }
        finally
        {
            _signing = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The Sign menu: saved signatures and initials to place, add/delete them, Sign Digitally.</summary>
    private void ShowSignatureMenu(DocumentTab t)
    {
        var menu = new ContextMenu();
        var saved = SignatureLibrary.List();
        foreach (var s in saved)
        {
            var sig = s;
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            try
            {
                header.Children.Add(new Border
                {
                    Background = System.Windows.Media.Brushes.White,
                    Padding = new Thickness(4, 2, 4, 2),
                    Child = new Image { Source = s.Load(), Height = 28, MaxWidth = 140, Stretch = System.Windows.Media.Stretch.Uniform },
                });
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                Log.Write($"Loading a saved signature failed: {ex.Message}");
                continue;
            }
            header.Children.Add(new TextBlock { Text = s.Name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header };
            System.Windows.Automation.AutomationProperties.SetName(item, $"Place {s.Name}");
            item.Click += (_, _) => t.BeginPlaceSignature(sig);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var signatures = saved.Count(s => !s.Initials);
        var add = new MenuItem { Header = "Add a _signature…", IsEnabled = signatures < SignatureLibrary.MaxSignatures };
        if (!add.IsEnabled) add.ToolTip = $"Up to {SignatureLibrary.MaxSignatures} signatures: delete one first.";
        add.Click += (_, _) => CaptureSignature(t, initials: false);
        menu.Items.Add(add);
        var initials = new MenuItem { Header = saved.Any(s => s.Initials) ? "Replace the _initials…" : "Add _initials…" };
        initials.Click += (_, _) => CaptureSignature(t, initials: true);
        menu.Items.Add(initials);
        if (saved.Count > 0)
        {
            var delete = new MenuItem { Header = "_Delete a saved signature" };
            foreach (var s in saved)
            {
                var sig = s;
                var d = new MenuItem { Header = s.Name };
                d.Click += (_, _) =>
                {
                    if (!Prompt.Confirm(this, $"Delete {sig.Name}?", "It is removed from this PC. Signatures already placed in documents stay.", "Delete")) return;
                    try
                    {
                        SignatureLibrary.Delete(sig);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Prompt.Error(this, "Couldn't delete it", ex.Message);
                    }
                };
                delete.Items.Add(d);
            }
            menu.Items.Add(delete);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Command = Cmd.Sign, CommandTarget = this });

        menu.PlacementTarget = AddSignatureButton.IsVisible ? AddSignatureButton : this;
        menu.Placement = AddSignatureButton.IsVisible ? PlacementMode.Bottom : PlacementMode.Center;
        menu.Closed += (_, _) => UpdateUi();
        menu.IsOpen = true;
        AddSignatureButton.IsChecked = true;
    }

    private void CaptureSignature(DocumentTab t, bool initials)
    {
        var dlg = new SignatureCaptureDialog(this, initials, DocumentTab.Author);
        if (dlg.ShowDialog() != true || dlg.Result is null || t.IsDisposed) return;
        SavedSignature? saved;
        try
        {
            saved = SignatureLibrary.Save(dlg.Result, initials);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Prompt.Error(this, "Couldn't save the signature", ex.Message);
            return;
        }
        if (saved is null)
        {
            Prompt.Info(this, "No room for another signature", $"Up to {SignatureLibrary.MaxSignatures} signatures can be saved: delete one first.");
            return;
        }
        t.BeginPlaceSignature(saved);
    }
}
