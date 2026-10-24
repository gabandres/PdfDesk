# Inkvolt PDF for the Microsoft Store (MSIX)

`pack-msix.ps1` builds Inkvolt PDF as MSIX packages for x64 and ARM64, bundles them, and writes the Store upload file. The
unpackaged per-user install (`install.ps1`) keeps working unchanged next to it.

Inkvolt PDF is licensed under the GNU AGPL v3 (`../LICENSE`; MuPDF requires it); dependencies are listed in
`../THIRD-PARTY-NOTICES.md`. Every Store build must point to its source: https://github.com/gabandres/inkvolt-pdf.

## Build

```powershell
pwsh -File packaging/pack-msix.ps1                       # Store build with the Partner Center identity, unsigned
pwsh -File packaging/pack-msix.ps1 -Version 1.1.0.0      # a later submission
```

The defaults are the app's Partner Center identity (below); pass `-IdentityName`, `-Publisher` and
`-PublisherDisplayName` only to build under another one.

Other parameters: `-DisplayName` (default `Inkvolt PDF`), `-Architectures x64,arm64`, `-OutDir`, `-NoBuild` (reuse the last
publish), `-SignWithTestCert` (below).

What it does:

1. Restores `Microsoft.Windows.SDK.BuildTools` (version in `tools/BuildTools.csproj`) into the NuGet cache and uses its
   `makeappx`, `makepri` and `signtool`. No Windows SDK install.
2. `dotnet publish` per architecture: Release, self-contained, folder layout (not single-file), version = `-Version`.
   Checks that the MuPDF and HarfBuzz natives are there.
3. Stages `publish/msix/layout/<arch>`: the publish output, `Assets/`, `resources.pri` (makepri index of the qualified
   images), `THIRD-PARTY-NOTICES.md`, and `AppxManifest.xml` filled from `AppxManifest.template.xml`.
4. `makeappx pack` per architecture (this also validates the manifest), `makeappx bundle`, then zips the bundle as
   the `.msixupload`.

Output in `publish/msix` (ignored by git):

| File | Use |
|---|---|
| `InkvoltPdf_<ver>.msixupload` | Upload this to Partner Center (a `.msixbundle` upload works too) |
| `InkvoltPdf_<ver>.msixbundle` | Both architectures; sideload test |
| `InkvoltPdf_<ver>_x64.msix`, `InkvoltPdf_<ver>_arm64.msix` | Single-architecture packages |

Images: `Assets/*.png` are generated, with the icons, by `python -I assets/make-icon.py`. Commit them after a change.

## Values from Partner Center

Reserve the name **Inkvolt PDF** (or another) in Partner Center, then open the app's **Product management > Product
identity** page and copy:

| Partner Center field | Script parameter | Manifest |
|---|---|---|
| Package/Identity/Name | `-IdentityName` | `Identity/@Name` |
| Package/Identity/Publisher (`CN=...`) | `-Publisher` | `Identity/@Publisher` |
| Package/Properties/PublisherDisplayName | `-PublisherDisplayName` | `Properties/PublisherDisplayName` |
| Reserved app name | `-DisplayName` | `Properties/DisplayName`, tile names |

Inkvolt PDF's values (the script's defaults):

| Field | Value |
|---|---|
| Store ID | `9MXDTSFJ9W28` (Store page: `ms-windows-store://pdp/?ProductId=9MXDTSFJ9W28`) |
| Package/Identity/Name | `BermudezSystems.InkvoltPDF` |
| Package/Identity/Publisher | `CN=1CC7D6EF-8A48-4BDE-AF19-4FEC30D5613B` |
| Package/Properties/PublisherDisplayName | `Bermudez Systems` |
| Reserved app name | `Inkvolt PDF` |

Inkvolt Print, the suite's other app, is Store ID `9PNR76HJ62SK` (`BermudezSystems.InkvoltPrint`, same publisher).

Version: four numbers, the last one 0 (the Store reserves it), higher than every earlier submission.

## Upload

1. Partner Center > the app > **Start your submission**.
2. **Packages**: drop `InkvoltPdf_<ver>.msixupload`. Device families: Windows 10/11 Desktop. Partner Center validates the
   identity values against the reservation; a mismatch is reported here.
3. **Properties**: category Productivity (or Business); privacy policy URL (see below); support contact.
4. **Age ratings**: the questionnaire (no user-generated content shared, no purchases, opens links in the browser).
5. **Store listings** (en-us): description, at least one screenshot, optional logos and trailers.
6. **Submission options > Restricted capabilities**: explain `runFullTrust` (text below).
7. Submit. Certification normally takes 1-3 business days.

### runFullTrust justification

> Inkvolt PDF is a classic Win32 desktop application (.NET / WPF) packaged with the Desktop Bridge. `runFullTrust` is
> required for every packaged desktop app: Inkvolt PDF runs as a regular full-trust process to render documents with its
> bundled native PDF engine (MuPDF), read and save files the user opens anywhere on disk, print through the Windows
> print system, use Windows OCR, and sign documents with certificates from the user's Windows certificate store and
> smart cards. It installs no services or drivers, needs no administrator rights, and changes no system settings.

### Draft Store description

Short description:

> A fast, private PDF reader and editor: fill and sign forms, comment, draw, organize pages, redact and OCR. No account,
> no ads, no telemetry.

Description:

> Inkvolt PDF opens big documents instantly and keeps your files on your PC. Read, fill, sign, mark up and rearrange
> PDFs with a clean Windows 11 interface, in tabs, in light or dark.
>
> - Read comfortably: continuous or two-page layouts, fast search, bookmarks, dark pages for night reading, Read Aloud.
> - Fill & Sign: fill any form, type anywhere on a page, and add a handwritten signature or initials.
> - Digital signatures: sign with a certificate or smart card, add a trusted timestamp, and check every signature's
>   status in detail.
> - Comment and draw: highlight, underline, sticky notes, pen, shapes, arrows, text boxes and callouts.
> - Organize: reorder, rotate, insert, extract, split and merge pages; make PDFs from images or the clipboard.
> - Redact for real: removes the text and images underneath, not just a black box on top.
> - Make scans searchable with Windows' own text recognition, protect files with a password, and reduce their size.
> - Scan straight into a PDF with Inkvolt Print.
>
> No account, no cloud, no ads, no telemetry. Your documents never leave your PC.
>
> Free and open source (AGPL-3.0). Source code: https://github.com/gabandres/inkvolt-pdf

Keywords (up to 7): PDF, PDF editor, fill and sign, digital signature, annotate, merge PDF, OCR.

### Privacy policy

Partner Center asks for a privacy policy URL; publish a short page (e.g. on the product site or a public gist) saying:

- Inkvolt PDF collects no personal data and has no telemetry, analytics, ads or accounts.
- Documents are processed on the PC (rendering, search, OCR with Windows' own recognizer).
- Network use happens only on the user's action: an RFC 3161 timestamp when signing with "Add a timestamp" (only a
  hash of the signature goes to the chosen timestamp server, default DigiCert); certificate revocation checks when
  verifying a signature on request (Windows fetches CRL/OCSP from the certificate's issuer); and links in documents,
  which open in the default browser after the user clicks (and confirms for anything but http/https/mailto).
- Settings, recent files and logs stay in the user's local app data folder.

### Store listing checklist

- [ ] Source link in the description: "Source code (AGPL-3.0): https://github.com/gabandres/inkvolt-pdf".
- [ ] Description, short description, feature list (from the README's "Using it").
- [ ] Screenshots: PNG, at least 1366x768 (1920x1080 recommended), 1-10. Take them off-screen with the dev script, e.g.
      a `steps.txt` of `size 1920 1080`, `wait 1500`, `shot C:\shots\reading.png`, `exit`, then
      `$env:INKVOLTPDF_DEVSCRIPT=1; $env:INKVOLTPDF_DATA="$env:TEMP\inkvoltpdf-shots"; InkvoltPdf.exe sample.pdf --script steps.txt`
      (see `src/InkvoltPdf.App/DevScript.cs` for panes, themes and tools).
- [ ] Store logo (optional 300x300 / 1:1 box art): `assets/InkvoltPdf-512.png` scaled.
- [ ] Privacy policy URL; support URL or email.
- [ ] Age rating questionnaire; pricing and availability (markets, free/paid).
- [ ] `runFullTrust` justification.
- [ ] Notes for certification: "No sign-in needed. Open any PDF from File > Open."

## Local test install (optional)

The Store signs uploads itself; local installs need a trusted signature or Developer Mode. Both are manual steps on
purpose: the script never installs certificates or packages.

**Signed bundle.** `pack-msix.ps1 -SignWithTestCert` creates `publish/msix/InkvoltPdf-TestSigning.pfx` (no password) and
`.cer` with subject = `-Publisher`, in memory and files only, then signs the bundle and both `.msix`. To install it,
trust the certificate, then add the package (elevated PowerShell for the first command):

```powershell
Import-Certificate publish\msix\InkvoltPdf-TestSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage publish\msix\InkvoltPdf_1.0.0.0.msixbundle
# Remove afterwards:
Get-AppxPackage BermudezSystems.InkvoltPDF | Remove-AppxPackage
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=1CC7D6EF-8A48-4BDE-AF19-4FEC30D5613B' | Remove-Item
```

Anyone holding the `.pfx` can sign packages this PC then trusts: remove the certificate when done, and keep the
`.pfx` out of git (it lives under the ignored `publish/`).

**Unsigned, Developer Mode.** With Settings > System > For developers > Developer Mode on, register the staged folder
directly: `Add-AppxPackage -Register publish\msix\layout\arm64\AppxManifest.xml` (x64 on x64 PCs).

If `install.ps1`'s copy is installed too, both appear under "Open with"; uninstall one (`install.ps1 -Uninstall`).

## How the packaged app differs

`AppEnvironment.IsPackaged` (src/InkvoltPdf.App/AppEnvironment.cs) tells the two apart at run time:

- File associations, the Start menu entry and the `inkvoltpdf` command (app execution alias) come from the manifest;
  nothing writes registry associations. Settings' "Open Default apps" opens the package's own Default apps page.
- Settings and logs stay at `%LOCALAPPDATA%\InkvoltPdf`; Windows redirects that into the package's private folder, so a
  packaged and an unpackaged copy keep separate settings. Uninstalling the package removes them.
- Single instance, file forwarding (named pipe) and everything else behave the same. Updates come from the Store.
- `AppEnvironment.FindSuiteApp("InkvoltPrint")` finds a sibling app through its execution alias
  (`%LOCALAPPDATA%\Microsoft\WindowsApps\inkvoltprint.exe`), App Paths, or its install folders.
