# Installs (or removes) Inkvolt PDF for the current user. No admin rights needed.
#   pwsh -File install.ps1              build the exe for this PC (ARM64 or x64), copy it, add Start Menu + Desktop shortcuts and "Open with" entries
#   pwsh -File install.ps1 -NoBuild     install the existing publish\win-<arch>\InkvoltPdf.exe
#   pwsh -File install.ps1 -Uninstall   remove all of the above
# (Microsoft Store / MSIX builds: packaging/pack-msix.ps1. Their file associations come from the package manifest.)
param([switch]$Uninstall, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'Programs\InkvoltPdf'
$exe = Join-Path $dest 'InkvoltPdf.exe'
$docIcon = Join-Path $dest 'PdfDocument.ico'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Inkvolt PDF.lnk'
$desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Inkvolt PDF.lnk'
$classes = 'HKCU:\Software\Classes'
$progId = 'InkvoltPdf.Document'
$types = '.pdf', '.xps', '.oxps', '.epub', '.cbz', '.fb2' # same list as packaging/AppxManifest.template.xml

if (Get-Process InkvoltPdf -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'Inkvolt PDF is running. Close it (save your work first), then run this again.'
}

if ($Uninstall) {
    Remove-Item $lnk, $desktopLnk -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "$classes\$progId" -Recurse -ErrorAction SilentlyContinue
    Remove-Item "$classes\Applications\InkvoltPdf.exe" -Recurse -ErrorAction SilentlyContinue
    foreach ($t in $types) { Remove-ItemProperty "$classes\$t\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue }
    Remove-Item 'HKCU:\Software\InkvoltPdf' -Recurse -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'Inkvolt PDF' -ErrorAction SilentlyContinue
    Write-Host 'Inkvolt PDF removed. (Settings and logs in %LOCALAPPDATA%\InkvoltPdf are kept.)'
    return
}

$rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
$publish = Join-Path $PSScriptRoot "publish\$rid"
$src = Join-Path $publish 'InkvoltPdf.exe'
if (-not $NoBuild) {
    # Start clean, so nothing left over from an older build ends up installed.
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $PSScriptRoot 'src\InkvoltPdf.App') -p:PublishProfile=$rid
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
if (-not (Test-Path $src)) { throw "Not found: $src (run without -NoBuild)" }

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $src $exe -Force
# The exe is single-file (natives bundled). Copy only a native library the bundle left beside it, never symbols (.pdb).
Get-ChildItem $publish -File -Filter *.dll | Copy-Item -Destination $dest -Force
Copy-Item (Join-Path $PSScriptRoot 'assets\PdfDocument.ico') $docIcon -Force

# Start Menu and Desktop shortcuts (the exe carries the Inkvolt PDF icon).
$shell = New-Object -ComObject WScript.Shell
foreach ($path in $lnk, $desktopLnk) {
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dest
    $s.IconLocation = "$exe,0"
    $s.Description = 'View, search, print and organize PDF files'
    $s.Save()
}

# "Open with" + Default Apps registration (per user). Windows still asks you to pick the default yourself.
# New-Item -Force on an existing key deletes its values (other apps' Open With entries, every registered app), so
# shared keys are only created when missing; our own keys are created the same way for consistency.
function Get-Key([string]$path) {
    if (-not (Test-Path $path)) { New-Item $path -Force | Out-Null }
    Get-Item $path
}
$open = "`"$exe`" `"%1`""
Get-Key "$classes\$progId\shell\open\command" | Set-ItemProperty -Name '(default)' -Value $open
Set-ItemProperty "$classes\$progId" -Name '(default)' -Value 'PDF Document'
Get-Key "$classes\$progId\DefaultIcon" | Set-ItemProperty -Name '(default)' -Value "`"$docIcon`""
Get-Key "$classes\Applications\InkvoltPdf.exe\shell\open\command" | Set-ItemProperty -Name '(default)' -Value $open
Get-Key "$classes\Applications\InkvoltPdf.exe\SupportedTypes" | Out-Null
Get-Key 'HKCU:\Software\InkvoltPdf\Capabilities\FileAssociations' | Out-Null
Set-ItemProperty 'HKCU:\Software\InkvoltPdf\Capabilities' -Name ApplicationName -Value 'Inkvolt PDF'
Set-ItemProperty 'HKCU:\Software\InkvoltPdf\Capabilities' -Name ApplicationDescription -Value 'View, search, print and organize PDF files'
foreach ($t in $types) {
    Get-Key "$classes\$t\OpenWithProgids" | New-ItemProperty -Name $progId -Value '' -PropertyType String -Force | Out-Null
    New-ItemProperty "$classes\Applications\InkvoltPdf.exe\SupportedTypes" -Name $t -Value '' -PropertyType String -Force | Out-Null
    Set-ItemProperty 'HKCU:\Software\InkvoltPdf\Capabilities\FileAssociations' -Name $t -Value $progId
}
Get-Key 'HKCU:\Software\RegisteredApplications' | Set-ItemProperty -Name 'Inkvolt PDF' -Value 'Software\InkvoltPdf\Capabilities'

Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);'
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED

Write-Host "Installed $exe"
Write-Host 'To make it the default PDF app: Settings > Apps > Default apps > Inkvolt PDF > .pdf'
