# Installs (or removes) PdfDesk for the current user. No admin rights needed.
#   pwsh -File install.ps1              build ARM64 exe, copy it, add Start Menu + Desktop shortcuts and "Open with" entries
#   pwsh -File install.ps1 -NoBuild     install the existing publish\win-arm64\PdfDesk.exe
#   pwsh -File install.ps1 -Uninstall   remove all of the above
param([switch]$Uninstall, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'Programs\PdfDesk'
$exe = Join-Path $dest 'PdfDesk.exe'
$docIcon = Join-Path $dest 'PdfDocument.ico'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'PdfDesk.lnk'
$desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PdfDesk.lnk'
$classes = 'HKCU:\Software\Classes'
$progId = 'PdfDesk.Document'
$types = '.pdf', '.xps', '.oxps', '.epub', '.cbz'

if (Get-Process PdfDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'PdfDesk is running. Close it (save your work first), then run this again.'
}

if ($Uninstall) {
    Remove-Item $lnk, $desktopLnk -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "$classes\$progId" -Recurse -ErrorAction SilentlyContinue
    Remove-Item "$classes\Applications\PdfDesk.exe" -Recurse -ErrorAction SilentlyContinue
    foreach ($t in $types) { Remove-ItemProperty "$classes\$t\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue }
    Remove-Item 'HKCU:\Software\PdfDesk' -Recurse -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name PdfDesk -ErrorAction SilentlyContinue
    Write-Host 'PdfDesk removed. (Settings and logs in %LOCALAPPDATA%\PdfDesk are kept.)'
    return
}

$publish = Join-Path $PSScriptRoot 'publish\win-arm64'
$src = Join-Path $publish 'PdfDesk.exe'
if (-not $NoBuild) {
    # Start clean, so nothing left over from an older build ends up installed.
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $PSScriptRoot 'src\PdfDesk.App') -p:PublishProfile=win-arm64
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
if (-not (Test-Path $src)) { throw "Not found: $src (run without -NoBuild)" }

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $src $exe -Force
# The exe is single-file (natives bundled). Copy only a native library the bundle left beside it, never symbols (.pdb).
Get-ChildItem $publish -File -Filter *.dll | Copy-Item -Destination $dest -Force
Copy-Item (Join-Path $PSScriptRoot 'assets\PdfDocument.ico') $docIcon -Force

# Start Menu and Desktop shortcuts (the exe carries the PdfDesk icon).
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
$open = "`"$exe`" `"%1`""
New-Item "$classes\$progId\shell\open\command" -Force | Set-ItemProperty -Name '(default)' -Value $open
Set-ItemProperty "$classes\$progId" -Name '(default)' -Value 'PDF Document'
New-Item "$classes\$progId\DefaultIcon" -Force | Set-ItemProperty -Name '(default)' -Value "`"$docIcon`""
New-Item "$classes\Applications\PdfDesk.exe\shell\open\command" -Force | Set-ItemProperty -Name '(default)' -Value $open
New-Item "$classes\Applications\PdfDesk.exe\SupportedTypes" -Force | Out-Null
New-Item 'HKCU:\Software\PdfDesk\Capabilities\FileAssociations' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\PdfDesk\Capabilities' -Name ApplicationName -Value 'PdfDesk'
Set-ItemProperty 'HKCU:\Software\PdfDesk\Capabilities' -Name ApplicationDescription -Value 'View, search, print and organize PDF files'
foreach ($t in $types) {
    New-Item "$classes\$t\OpenWithProgids" -Force | New-ItemProperty -Name $progId -Value '' -PropertyType String -Force | Out-Null
    New-ItemProperty "$classes\Applications\PdfDesk.exe\SupportedTypes" -Name $t -Value '' -PropertyType String -Force | Out-Null
    Set-ItemProperty 'HKCU:\Software\PdfDesk\Capabilities\FileAssociations' -Name $t -Value $progId
}
New-Item 'HKCU:\Software\RegisteredApplications' -Force | Set-ItemProperty -Name PdfDesk -Value 'Software\PdfDesk\Capabilities'

Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);'
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED

Write-Host "Installed $exe"
Write-Host 'To make it the default PDF app: Settings > Apps > Default apps > PdfDesk > .pdf'
