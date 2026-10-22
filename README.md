# PdfDesk

A fast PDF viewer and page organizer for Windows on ARM, modeled on PDF-XChange Editor's viewing and
Organize features. Native ARM64 (.NET 10 WPF + MuPDF), personal use, local git repo, no remote.

## Install

```powershell
pwsh -File install.ps1            # builds publish\win-arm64\PdfDesk.exe, installs to %LOCALAPPDATA%\Programs\PdfDesk
pwsh -File install.ps1 -Uninstall
```

Adds a Start Menu shortcut and "Open with" entries for .pdf/.xps/.epub/.cbz. To make it the default:
Settings › Apps › Default apps › PdfDesk.

## Features

**Viewing**: tabs, continuous / single page / two pages / two pages with cover, zoom (presets, Ctrl+wheel at the
cursor, fit width, fit page, up to 6400%), thumbnails, bookmarks, clickable links (external links other than
http/https/mailto ask first), Back/Forward (Alt+←/→), full screen (F11), Find bar (Ctrl+F) and Search pane
(Ctrl+Shift+F) with match case / whole words, text selection and copy (word/line on double/triple click),
snapshot tool (copies an area as an image), print (shrinks oversized pages, auto-rotates), export pages as
PNG/JPEG, document properties (title/author/subject/keywords editable), recent files, password-protected PDFs.
Also opens XPS, EPUB, CBZ, FB2 and images (view only).

**Organize** (all undoable, Ctrl+Z / Ctrl+Y): rotate, delete, insert from file, insert blank, extract (new tab /
one file / file per page, optionally delete), split (every N pages / ranges / top-level bookmarks), move, duplicate,
reverse, crop and uncrop (margins follow page rotation), combine files (bookmark per file). Drag thumbnails to
reorder, drag them onto another tab to copy pages there, drop PDF files onto thumbnails to insert them.

Keyboard shortcuts: Help › Keyboard Shortcuts.

## Layout

| Path | What |
|---|---|
| `src/PdfDesk.Core` | `PdfEngine` (MuPDF: render, text, links, outline), `PageEditor` (PDFsharp page edits, bytes in → bytes out), `DocumentSession` (bytes + undo snapshots), `PageText`, `PageRange` |
| `src/PdfDesk.App` | WPF UI. `Viewer/PageViewer.cs` is the virtualized page view; `DocumentTab` holds panes; `Organize.cs` / `FileActions.cs` are the commands |
| `tests/PdfDesk.Tests` | `dotnet test tests/PdfDesk.Tests` |

Dev check without touching the screen: `PdfDesk.exe file.pdf --script steps.txt` runs UI steps off-screen and
writes snapshots (see `DevScript.cs`).

## Licenses

MuPDF (via MuPDFCore) is AGPL-3.0: fine for personal use; don't distribute PdfDesk closed-source.
PDFsharp is pinned to 6.2.x (MIT); 6.5+ changed to AGPL with a runtime license check.
`System.Security.Cryptography.Pkcs` is pinned to a patched release (PDFsharp pulls a vulnerable 8.0.1).

## Known limits

- Page edits go through PDFsharp, which rewrites the whole file, so its size can change. Digital signatures are
  invalidated by any page edit, as in every editor.
- Editing pages of a password-protected PDF needs its owner (permissions) password; opening it with the user
  password allows viewing, printing and copying only. Edits are saved without the password protection
  (PdfDesk warns before the first edit).
- Annotations, form filling, OCR, redaction and text editing are not in v1.
