# Third-party notices

Inkvolt PDF includes the following third-party software. Each component is used under the license named here; the
license texts are at the links given. Versions are the ones Inkvolt PDF builds with (license data read from each NuGet
package's metadata).

| Component | Version | License | Copyright | Source |
|---|---|---|---|---|
| MuPDF (via MuPDFCore's native library MuPDFWrapper.dll) | 1.25.2 | AGPL-3.0-only | Artifex Software, Inc. | https://mupdf.com, https://github.com/ArtifexSoftware/mupdf |
| MuPDFCore (.NET bindings) | 2.0.1 | AGPL-3.0-only | Giorgio Bianchini | https://github.com/arklumpus/MuPDFCore |
| PDFsharp | 6.2.4 | MIT | empira Software GmbH | https://github.com/empira/PDFsharp |
| Jint | 4.17.0 | BSD-2-Clause | Sebastien Ros | https://github.com/sebastienros/jint |
| Acornima (used by Jint) | 1.7.0 | BSD-3-Clause | Adam Simon | https://github.com/adams85/acornima |
| HarfBuzzSharp (incl. native libHarfBuzzSharp) | 14.2.1.301 | MIT | Microsoft Corporation; Xamarin, Inc. | https://github.com/mono/SkiaSharp |
| HarfBuzz (inside libHarfBuzzSharp) | | MIT ("Old MIT") | HarfBuzz contributors | https://github.com/harfbuzz/harfbuzz |
| System.Security.Cryptography.Pkcs | 10.0.13 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.Abstractions (used by PDFsharp) | 8.0.3 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions (used by PDFsharp) | 8.0.2 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| .NET runtime and WPF (self-contained) | 10.0 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| Windows SDK projection (Microsoft.Windows.SDK.NET) | 10.0.22621 | Microsoft Windows SDK license | Microsoft Corporation | https://aka.ms/WinSDKLicenseURL |

## Libraries built into MuPDF

MuPDFWrapper.dll statically links MuPDF and its third-party libraries. Those found in the shipped binary:

| Component | License |
|---|---|
| jbig2dec | AGPL-3.0 (Artifex) |
| extract | AGPL-3.0 (Artifex) |
| FreeType | FreeType License (FTL) |
| HarfBuzz | MIT ("Old MIT") |
| Little CMS (lcms2mt, Artifex fork) | MIT |
| libjpeg (Independent JPEG Group) | IJG License |
| OpenJPEG | BSD-2-Clause |
| zlib 1.2.13 | Zlib |
| MuJS | ISC |
| Tesseract OCR | Apache-2.0 |
| Leptonica | BSD-2-Clause (Leptonica license) |
| libarchive | BSD-2-Clause |

The complete list and license texts are in the MuPDF 1.25.2 source distribution (`thirdparty/` and `COPYING`).

## MuPDF and the AGPL

MuPDF is licensed under the GNU Affero General Public License v3.0 (https://www.gnu.org/licenses/agpl-3.0.html).
Its complete source code is available from https://mupdf.com/releases and https://github.com/ArtifexSoftware/mupdf
(tag 1.25.2); MuPDFCore's source, including the build scripts for MuPDFWrapper.dll, from
https://github.com/arklumpus/MuPDFCore (tag v2.0.1).

## Full license texts

- MIT: https://opensource.org/license/mit
- BSD-2-Clause: https://opensource.org/license/bsd-2-clause
- BSD-3-Clause: https://opensource.org/license/bsd-3-clause
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
- AGPL-3.0: https://www.gnu.org/licenses/agpl-3.0.html
- ISC: https://opensource.org/license/isc-license-txt
- Zlib: https://zlib.net/zlib_license.html
- FreeType License: https://freetype.org/license.html
- IJG: https://github.com/libjpeg-turbo/libjpeg-turbo/blob/main/README.ijg
- .NET third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
