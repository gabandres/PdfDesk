using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PdfDesk.Core;

namespace PdfDesk.App;

/// <summary>The open password an edit of an AES-256 document needs, or the choice to save it without one.</summary>
public sealed record OpenPasswordAnswer(string? Password, bool DropIt);

/// <summary>File › Protect with Password / Remove Protection, the security line in Properties, and the open-password prompt.</summary>
public static class SecurityActions
{
    /// <summary>The document's protection, or null when it can't be read (not a PDF, damaged).</summary>
    public static SecurityInfo? Read(DocumentSession s)
    {
        if (!s.IsPdf) return null;
        try
        {
            return PdfSecurity.ReadSecurity(s.Data, s.Password ?? s.OpenPassword);
        }
        catch (Exception ex) when (ex is PasswordRequiredException or InvalidOperationException or PdfSharp.PdfSharpException)
        {
            Log.Write($"Security unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>One line for Document Properties: "AES-256 · needs a password to open · no copying".</summary>
    public static string? Describe(DocumentSession s)
    {
        if (!s.IsPdf) return s.Engine.IsEncrypted ? "Encrypted" : null;
        if (Read(s) is not { } info) return s.Engine.IsEncrypted ? "Encrypted" : null;
        if (!info.IsEncrypted) return "Not protected";
        var parts = new List<string>
        {
            info.Method switch
            {
                EncryptionMethod.Aes256 => "AES-256",
                EncryptionMethod.Aes128 => "AES-128",
                EncryptionMethod.Rc4With128Bits => "RC4 128-bit (outdated)",
                EncryptionMethod.Rc4With40Bits => "RC4 40-bit (outdated)",
                _ => "Encrypted",
            },
            info.RequiresOpenPassword ? "needs a password to open" : "opens without a password",
        };
        var p = info.Permissions;
        if (p.IsUnrestricted) parts.Add("no restrictions");
        else
        {
            if (p.Print == PrintPermission.None) parts.Add("no printing");
            else if (p.Print == PrintPermission.LowResolution) parts.Add("low-resolution printing");
            if (!p.Copy) parts.Add("no copying");
            if (!p.Modify) parts.Add("no editing");
            if (!p.Annotate) parts.Add("no comments");
            if (!p.FillForms) parts.Add("no form filling");
            if (!p.Assemble) parts.Add("no page changes");
        }
        return string.Join(" · ", parts);
    }

    public static async Task Protect(Window owner, DocumentTab tab)
    {
        if (!tab.Session.IsPdf || !await tab.SettleAsync()) return;
        var current = Read(tab.Session);
        var encrypted = current?.IsEncrypted == true;
        var perms = encrypted ? current!.Permissions : PdfPermissions.All;

        var dlg = new FormDialog(owner, encrypted ? "Change Password Protection" : "Protect with Password", "Protect", 500);
        dlg.AddNote(encrypted
            ? "Set new passwords and permissions. The old passwords stop working once you save."
            : "Encrypts the document. Keep the passwords somewhere safe: a forgotten open password can't be recovered.");

        // Open (user) password
        var needOpen = dlg.Add("Opening", new CheckBox { Content = "Require a password to open the document", IsChecked = current?.RequiresOpenPassword == true });
        var openBox = new PasswordBox { MaxLength = 127 };
        var openConfirm = new PasswordBox { MaxLength = 127 };
        var meter = new StrengthMeter();
        var openPanel = Fields(("Open password", openBox), ("Confirm", openConfirm));
        openPanel.Children.Add(meter);
        dlg.Add(null, openPanel);
        openBox.PasswordChanged += (_, _) => meter.Show(openBox.Password);

        // Permissions (owner) password and what it restricts
        var restrict = dlg.Add("Permissions", new CheckBox { Content = "Restrict printing, copying and editing", IsChecked = !perms.IsUnrestricted });
        var ownerBox = new PasswordBox { MaxLength = 127 };
        var ownerConfirm = new PasswordBox { MaxLength = 127 };
        var permPanel = Fields(("Permissions password", ownerBox), ("Confirm", ownerConfirm));
        var print = new ComboBox
        {
            ItemsSource = new[] { "High resolution", "Low resolution (150 dpi)", "Not allowed" },
            SelectedIndex = perms.Print switch { PrintPermission.HighResolution => 0, PrintPermission.LowResolution => 1, _ => 2 },
            Margin = new Thickness(0, 4, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 220,
        };
        permPanel.Children.Add(new TextBlock { Text = "Printing", Margin = new Thickness(0, 8, 0, 2) });
        permPanel.Children.Add(print);
        CheckBox Allow(string text, bool on)
        {
            var c = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 2, 0, 2) };
            permPanel.Children.Add(c);
            return c;
        }
        var copy = Allow("Copy text and images", perms.Copy);
        var edit = Allow("Change the document", perms.Modify);
        var comment = Allow("Add comments", perms.Annotate);
        var fill = Allow("Fill in form fields and sign", perms.FillForms);
        var assemble = Allow("Insert, delete and rotate pages", perms.Assemble);
        dlg.Add(null, permPanel);

        var method = dlg.AddChoice("Encryption",
            ["AES-256 (Acrobat X and later, recommended)", "AES-128 (for Acrobat 7-9 and older readers)"],
            current?.Method == EncryptionMethod.Aes128 ? 1 : 0);

        void Sync()
        {
            openPanel.Visibility = needOpen.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            permPanel.Visibility = restrict.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        needOpen.Click += (_, _) => Sync();
        restrict.Click += (_, _) => Sync();
        Sync();

        dlg.Validate(() =>
        {
            var wantOpen = needOpen.IsChecked == true;
            var wantRestrict = restrict.IsChecked == true;
            if (!wantOpen && !wantRestrict) return "Choose an open password, restrictions, or both.";
            if (wantOpen)
            {
                if (openBox.Password.Length == 0) return "Enter the open password.";
                if (openBox.Password != openConfirm.Password) return "The open passwords don't match.";
            }
            if (wantRestrict)
            {
                if (ownerBox.Password.Length == 0) return "Enter a permissions password: it is needed to lift the restrictions later.";
                if (ownerBox.Password != ownerConfirm.Password) return "The permissions passwords don't match.";
                if (wantOpen && ownerBox.Password == openBox.Password) return "Use a permissions password different from the open password, or anyone who can open the document could lift the restrictions.";
            }
            return null;
        });
        if (!dlg.ShowModal()) return;

        var user = needOpen.IsChecked == true ? openBox.Password : null;
        var ownerPw = restrict.IsChecked == true ? ownerBox.Password : null;
        var permissions = restrict.IsChecked == true
            ? new PdfPermissions(
                print.SelectedIndex switch { 0 => PrintPermission.HighResolution, 1 => PrintPermission.LowResolution, _ => PrintPermission.None },
                copy.IsChecked == true, edit.IsChecked == true, comment.IsChecked == true, fill.IsChecked == true, assemble.IsChecked == true)
            : PdfPermissions.All;
        var encryption = method.SelectedIndex == 1 ? EncryptionMethod.Aes128 : EncryptionMethod.Aes256;
        // Without a permissions password the open password doubles as one (nothing is restricted then).
        var sessionPassword = ownerPw ?? user;
        if (await tab.RunEdit(encrypted ? "Change password protection" : "Protect with password",
                (d, pw) => PdfSecurity.Protect(d, pw, user, ownerPw, permissions, encryption), quiet: true,
                newPasswords: (sessionPassword, user)))
            tab.ShowNotice(user is not null
                ? "Protected: the saved file needs its password to open. Save to keep it."
                : "Restrictions set. Save to keep them.");
    }

    public static async Task RemoveProtection(Window owner, DocumentTab tab)
    {
        if (!tab.Session.IsPdf || !await tab.SettleAsync()) return;
        if (!Prompt.Confirm(owner, "Remove password protection?",
                "The saved file opens without a password, and anyone can print, copy and change it.", "Remove protection", warning: true)) return;
        await tab.RunEdit("Remove password protection", (d, pw) => PdfSecurity.Unprotect(d, pw), newPasswords: (null, null));
    }

    /// <summary>
    /// Asks for the open password an edit of an AES-256 document opened with its owner password needs (checked against
    /// the file), or lets the user save without one. Null when cancelled.
    /// </summary>
    public static OpenPasswordAnswer? PromptOpenPassword(Window? owner, string name, byte[] data)
    {
        if (DevScript.TakeOpenPassword(out var scripted)) return scripted;
        var dlg = new FormDialog(owner!, "Keep the open password?", "Keep it", 480);
        dlg.AddNote($"\"{name}\" needs a password to open. You opened it with its permissions password, which can't reveal the open " +
                    "password, so enter it once to keep it on the edited file.");
        var box = dlg.Add("Open password", new PasswordBox());
        OpenPasswordAnswer? answer = null;
        var drop = dlg.Add(null, new Button
        {
            Content = "Save without an open password instead",
            Style = (Style)Application.Current.FindResource("TextButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "The permissions password and the restrictions stay; anyone can open the edited file.",
        });
        drop.Click += (_, _) =>
        {
            answer = new OpenPasswordAnswer(null, true);
            dlg.DialogResult = true;
        };
        dlg.Loaded += (_, _) => box.Focus();
        dlg.Validate(() => box.Password.Length == 0 ? "Enter the open password."
            : PdfSecurity.IsOpenPassword(data, box.Password) ? null : "That isn't this document's open password.");
        if (!dlg.ShowModal()) return null;
        return answer ?? new OpenPasswordAnswer(box.Password, false);
    }

    /// <summary>Labeled password rows in a panel that can be enabled as a whole.</summary>
    private static StackPanel Fields(params (string Label, Control Box)[] rows)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 4, 0, 0) };
        foreach (var (label, box) in rows)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            panel.Children.Add(box);
        }
        return panel;
    }

    /// <summary>Four segments and a word: how strong the typed password looks.</summary>
    private sealed class StrengthMeter : DockPanel
    {
        private readonly Border[] _bars = new Border[4];
        private readonly TextBlock _label = new() { Margin = new Thickness(10, 0, 0, 0), MinWidth = 50, FontSize = 12 };

        public StrengthMeter()
        {
            Margin = new Thickness(0, 8, 0, 0);
            SetDock(_label, Dock.Right);
            Children.Add(_label);
            var bars = new UniformGrid { Columns = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 4; i++)
            {
                _bars[i] = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 4, 0) };
                bars.Children.Add(_bars[i]);
            }
            Children.Add(bars);
            _label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Show("");
        }

        public void Show(string password)
        {
            var (score, label) = PasswordStrength.Rate(password);
            _label.Text = label;
            var key = score switch { 1 => "SystemFillColorCriticalBrush", 2 => "SystemFillColorCautionBrush", _ => "SystemFillColorSuccessBrush" };
            for (var i = 0; i < 4; i++)
                _bars[i].SetResourceReference(Border.BackgroundProperty, i < score ? key : "ControlStrongFillColorDisabledBrush");
            System.Windows.Automation.AutomationProperties.SetName(this, label.Length == 0 ? "Password strength" : $"Password strength: {label}");
        }
    }
}
