# PdfDesk's own license: open decision

PdfDesk has no license of its own yet, on purpose. The owner has to choose before PdfDesk is distributed to anyone
(Microsoft Store included). The deciding factor is MuPDF, which is AGPL-3.0 (see THIRD-PARTY-NOTICES.md); every
other dependency is permissive (MIT, BSD, Apache, ISC, Zlib, FTL, IJG).

## Option A: open source under AGPL-3.0

- License PdfDesk itself under AGPL-3.0-or-later (or GPL-3.0-compatible terms that allow combining with AGPL code).
- Publish the complete corresponding source (PdfDesk plus the exact MuPDF / MuPDFCore versions and build scripts) and
  offer it to every user, e.g. a public repository linked from the Store listing and the About box.
- Cost: none. Consequence: anyone may fork, rebuild and redistribute PdfDesk, also commercially, under the same terms.
- Store: allowed. The Store's terms do not conflict with AGPL as long as the source offer is made and no extra
  restrictions are placed on users' AGPL rights.

## Option B: closed source with a commercial MuPDF license

- Buy a commercial license for MuPDF from Artifex (https://artifex.com/licensing), which replaces the AGPL terms for
  MuPDF (and the AGPL parts built into it: jbig2dec, extract). MuPDFCore's own bindings are also AGPL-3.0: ask its
  author for a commercial license or replace them with your own bindings to the commercially licensed MuPDF.
- PdfDesk can then use any license, including proprietary, and be sold or given away without publishing its source.
- Cost: Artifex license fee (quoted per product/distribution).

## Until then

- Personal use only; don't distribute builds (Store submission included) or publish the source under any license.
- PDFsharp stays pinned to 6.2.x (MIT): 6.5+ is AGPL with a runtime license check, which would add a second
  AGPL/commercial dependency.
