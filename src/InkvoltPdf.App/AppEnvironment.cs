using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace InkvoltPdf.App;

/// <summary>
/// How Inkvolt PDF was installed: as an MSIX package (Microsoft Store or sideloaded) or unpackaged by install.ps1 /
/// run from a build folder. Packaged copies get their file associations, Start menu entry and app execution alias
/// (inkvoltpdf.exe) from the package manifest, never from the registry, and are updated by the Store.
/// Standalone (depends only on the Win32 API), so any part of the app can ask.
/// </summary>
public static class AppEnvironment
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>True when running with package identity (MSIX). Cheap: one Win32 call, cached.</summary>
    public static bool IsPackaged => PackageFamilyName is not null;

    /// <summary>The package family name (Name_PublisherHash) when packaged, else null.</summary>
    public static string? PackageFamilyName { get; } = QueryPackageFamilyName();

    /// <summary>
    /// The Settings page where the user picks Inkvolt PDF as the default app. Packaged: the package's own entry (by app user
    /// model id, PackageFamilyName!App as in AppxManifest.xml); unpackaged: the "RegisteredApplications" name install.ps1
    /// registers.
    /// </summary>
    public static string DefaultAppsUri => PackageFamilyName is { } family
        ? $"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(family + "!App")}"
        : $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString("Inkvolt PDF")}";

    /// <summary>
    /// Finds another app of the suite (e.g. "InkvoltPrint") whichever way it was installed. Order: the packaged app
    /// execution alias (%LOCALAPPDATA%\Microsoft\WindowsApps\name.exe, present when the MSIX declares one), App Paths
    /// (HKCU, then HKLM), then the suite installer's and install.ps1's per-user folders (%LOCALAPPDATA%\Programs\Inkvolt\name\name.exe,
    /// %LOCALAPPDATA%\Programs\name\name.exe). Null when absent.
    /// </summary>
    public static string? FindSuiteApp(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        var bare = Path.GetFileNameWithoutExtension(exe);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Execution aliases are zero-byte reparse points: File.Exists sees them, and starting one launches the package.
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", exe.ToLowerInvariant());
        if (File.Exists(alias)) return alias;

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (key?.GetValue(null) is string path && (path = path.Trim('"')).Length > 0 && File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable key: try the next place.
            }
        }

        foreach (var installed in new[] { Path.Combine(local, "Programs", "Inkvolt", bare, exe), Path.Combine(local, "Programs", bare, exe) })
            if (File.Exists(installed)) return installed;
        return null;
    }

    private static string? QueryPackageFamilyName()
    {
        try
        {
            var length = 0;
            var rc = GetCurrentPackageFamilyName(ref length, null);
            if (rc == AppModelErrorNoPackage || rc != ErrorInsufficientBuffer || length <= 0) return null;
            var buffer = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null; // Windows older than 8: never packaged
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);
}
