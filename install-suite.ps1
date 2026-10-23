# Installs (or removes) the Desk suite, PdfDesk and PrintDesk, for the current user. No admin rights needed.
#   pwsh -File install-suite.ps1              publish both (ARM64), install to %LOCALAPPDATA%\Programs\Desk\PdfDesk and \PrintDesk,
#                                             Start Menu folder "Desk", App Paths (so each app finds the other), PdfDesk "Open with"
#   pwsh -File install-suite.ps1 -NoBuild     install the existing publish outputs
#   pwsh -File install-suite.ps1 -Uninstall   remove what this script added, and restore registry values it replaced
# Each repo's own installer keeps working on its own (PdfDesk: install.ps1 → %LOCALAPPDATA%\Programs\PdfDesk).
# PrintDeskRoot: the PrintDesk checkout (default: $env:PrintDeskRoot, else a PrintDesk\ folder beside a folder above this
# script, else Z:\PrintDesk). InstallRoot, StartMenu and RegistryRoot exist for testing on scratch locations.
param(
    [switch]$Uninstall,
    [switch]$NoBuild,
    [string]$PrintDeskRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\Desk'),
    [string]$StartMenu = (Join-Path ([Environment]::GetFolderPath('Programs')) 'Desk'),
    [string]$RegistryRoot = 'HKCU:\Software'
)
$ErrorActionPreference = 'Stop'

# --- Registry: per user only, through the .NET API. Keys are created only when missing (never re-created, which would
# wipe their values); every key created and every value set is recorded, with the value it replaced, so -Uninstall undoes
# exactly that and nothing else.
if ($RegistryRoot -notmatch '^HKCU:\\(.+)$') { throw "RegistryRoot must be under HKCU:\ (got $RegistryRoot)" }
$regBase = $Matches[1].TrimEnd('\')
$hkcu = [Microsoft.Win32.Registry]::CurrentUser
$manifestPath = Join-Path $InstallRoot 'desk-suite.json'
$changes = [System.Collections.Generic.List[object]]::new()

function Read-Manifest {
    if (-not (Test-Path -LiteralPath $manifestPath)) { return @() }
    $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    return @($m.Registry)
}

function Ensure-Key([string]$sub) {
    $full = "$regBase\$sub"
    $k = $hkcu.OpenSubKey($full)
    if ($k) { $k.Dispose(); return }
    $parent = Split-Path $sub -Parent
    if ($parent) { Ensure-Key $parent }
    $hkcu.CreateSubKey($full).Dispose()
    $changes.Add([ordered]@{ Kind = 'key'; Path = $sub })
}

function Save-Data($value, [Microsoft.Win32.RegistryValueKind]$kind) {
    switch ($kind) {
        { $_ -in 'Binary', 'None' } { return [Convert]::ToBase64String([byte[]]$value) }
        'MultiString' { return @($value) }
        default { return $value }
    }
}

function Restore-Data($data, [Microsoft.Win32.RegistryValueKind]$kind) {
    switch ($kind) {
        { $_ -in 'Binary', 'None' } { return [Convert]::FromBase64String([string]$data) }
        'MultiString' { return [string[]]@($data) }
        'DWord' { return [int]$data }
        'QWord' { return [long]$data }
        default { return [string]$data }
    }
}

# $name '' is the key's default value.
function Set-Value([string]$sub, [string]$name, [string]$value) {
    Ensure-Key $sub
    $k = $hkcu.OpenSubKey("$regBase\$sub", $true)
    try {
        $existed = $k.GetValueNames() -contains $name
        $old = $null; $oldKind = $null
        if ($existed) {
            $oldKind = $k.GetValueKind($name)
            $old = $k.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
            if ($oldKind -eq 'String' -and [string]$old -eq $value) { return } # already so: nothing to do or undo
        }
        $k.SetValue($name, $value, [Microsoft.Win32.RegistryValueKind]::String)
        $changes.Add([ordered]@{
            Kind = 'value'; Path = $sub; Name = $name; Value = $value; Existed = $existed
            OldKind = if ($existed) { "$oldKind" } else { $null }
            Old = if ($existed) { Save-Data $old $oldKind } else { $null }
        })
    } finally { $k.Dispose() }
}

function Undo-Registry($entries) {
    $list = @($entries)
    for ($i = $list.Count - 1; $i -ge 0; $i--) {
        $e = $list[$i]
        $full = "$regBase\$($e.Path)"
        if ($e.Kind -eq 'value') {
            $k = $hkcu.OpenSubKey($full, $true)
            if (-not $k) { continue }
            try {
                if (-not ($k.GetValueNames() -contains $e.Name)) { continue }
                # Changed since by someone else (another installer, the user): theirs now, leave it.
                if ([string]$k.GetValue($e.Name, $null, 'DoNotExpandEnvironmentNames') -ne $e.Value) { continue }
                if ($e.Existed) { $k.SetValue($e.Name, (Restore-Data $e.Old $e.OldKind), [Microsoft.Win32.RegistryValueKind]$e.OldKind) }
                else { $k.DeleteValue($e.Name, $false) }
            } finally { $k.Dispose() }
        } else {
            # A key we created goes only if nothing else has put anything in it since.
            $k = $hkcu.OpenSubKey($full)
            if (-not $k) { continue }
            $empty = $k.ValueCount -eq 0 -and $k.SubKeyCount -eq 0
            $k.Dispose()
            if ($empty) { $hkcu.DeleteSubKey($full, $false) }
        }
    }
}

function Write-Manifest($registry) {
    New-Item -ItemType Directory -Force $InstallRoot | Out-Null
    [ordered]@{ Installed = (Get-Date).ToString('s'); InstallRoot = $InstallRoot; StartMenu = $StartMenu; Registry = @($registry) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

$apps = [ordered]@{ PdfDesk = Join-Path $InstallRoot 'PdfDesk'; PrintDesk = Join-Path $InstallRoot 'PrintDesk' }
$running = Get-Process PdfDesk, PrintDesk -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "$(($running.Name | Sort-Object -Unique) -join ' and ') is running from $InstallRoot. Close it (save your work first), then run this again." }

if ($Uninstall) {
    Undo-Registry (Read-Manifest)
    foreach ($name in $apps.Keys) { Remove-Item -LiteralPath (Join-Path $StartMenu "$name.lnk") -ErrorAction SilentlyContinue }
    if ((Test-Path -LiteralPath $StartMenu) -and -not (Get-ChildItem -LiteralPath $StartMenu -Force)) { Remove-Item -LiteralPath $StartMenu }
    foreach ($dir in $apps.Values) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $manifestPath -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $InstallRoot) -and -not (Get-ChildItem -LiteralPath $InstallRoot -Force)) { Remove-Item -LiteralPath $InstallRoot }
    Write-Host 'Desk suite removed. (Settings and logs in %LOCALAPPDATA%\PdfDesk and \PrintDesk are kept.)'
    return
}

# --- Build ---
if (-not $PrintDeskRoot) { $PrintDeskRoot = $env:PrintDeskRoot }
if (-not $PrintDeskRoot) {
    for ($dir = $PSScriptRoot; $dir; $dir = Split-Path $dir -Parent) {
        if (Test-Path -LiteralPath (Join-Path $dir 'PrintDesk\PrintDesk.slnx')) { $PrintDeskRoot = Join-Path $dir 'PrintDesk'; break }
    }
}
if (-not $PrintDeskRoot) { $PrintDeskRoot = 'Z:\PrintDesk' }
if (-not (Test-Path -LiteralPath (Join-Path $PrintDeskRoot 'PrintDesk.slnx'))) { throw "PrintDesk not found at $PrintDeskRoot (pass -PrintDeskRoot)" }

$publish = @{ PdfDesk = Join-Path $PSScriptRoot 'publish\win-arm64'; PrintDesk = Join-Path $PrintDeskRoot 'publish\win-arm64' }
$projects = @{ PdfDesk = Join-Path $PSScriptRoot 'src\PdfDesk.App'; PrintDesk = Join-Path $PrintDeskRoot 'src\PrintDesk.App' }
if (-not $NoBuild) {
    foreach ($name in $apps.Keys) {
        # Start clean, so nothing left over from an older build ends up installed.
        Remove-Item -LiteralPath $publish[$name] -Recurse -Force -ErrorAction SilentlyContinue
        dotnet publish $projects[$name] -p:PublishProfile=win-arm64
        if ($LASTEXITCODE -ne 0) { throw "$name build failed." }
    }
}
foreach ($name in $apps.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish[$name] "$name.exe"))) { throw "Not found: $(Join-Path $publish[$name] "$name.exe") (run without -NoBuild)" }
}

# --- Files: each app's single-file exe plus any native library its bundle left beside it (never symbols) ---
foreach ($name in $apps.Keys) {
    $dest = $apps[$name]
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish[$name] "$name.exe") -Destination $dest -Force
    Get-ChildItem -LiteralPath $publish[$name] -File -Filter *.dll | Copy-Item -Destination $dest -Force
}
$pdfExe = Join-Path $apps.PdfDesk 'PdfDesk.exe'
$printExe = Join-Path $apps.PrintDesk 'PrintDesk.exe'
$docIcon = Join-Path $apps.PdfDesk 'PdfDocument.ico'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\PdfDocument.ico') -Destination $docIcon -Force

# --- Start Menu folder "Desk" (each shortcut carries its exe's own icon) ---
New-Item -ItemType Directory -Force $StartMenu | Out-Null
$shell = New-Object -ComObject WScript.Shell
foreach ($s in @(
        @{ Name = 'PdfDesk'; Exe = $pdfExe; Text = 'View, search, print, scan and organize PDF files' },
        @{ Name = 'PrintDesk'; Exe = $printExe; Text = 'Printers and scanners: status, scan, print, settings' })) {
    $l = $shell.CreateShortcut((Join-Path $StartMenu "$($s.Name).lnk"))
    $l.TargetPath = $s.Exe
    $l.WorkingDirectory = Split-Path $s.Exe
    $l.IconLocation = "$($s.Exe),0"
    $l.Description = $s.Text
    $l.Save()
}

# --- Registry ---
$appPaths = 'Microsoft\Windows\CurrentVersion\App Paths'
foreach ($exe in $pdfExe, $printExe) {
    $key = "$appPaths\$(Split-Path $exe -Leaf)"
    Set-Value $key '' $exe
    Set-Value $key 'Path' (Split-Path $exe)
}
# PdfDesk "Open with" + Default Apps, as install.ps1 registers it (same ProgId, so either install serves).
$classes = 'Classes'
$progId = 'PdfDesk.Document'
$open = "`"$pdfExe`" `"%1`""
Set-Value "$classes\$progId" '' 'PDF Document'
Set-Value "$classes\$progId\shell\open\command" '' $open
Set-Value "$classes\$progId\DefaultIcon" '' "`"$docIcon`""
Set-Value "$classes\Applications\PdfDesk.exe\shell\open\command" '' $open
Set-Value 'PdfDesk\Capabilities' 'ApplicationName' 'PdfDesk'
Set-Value 'PdfDesk\Capabilities' 'ApplicationDescription' 'View, search, print and organize PDF files'
foreach ($t in '.pdf', '.xps', '.oxps', '.epub', '.cbz') {
    Set-Value "$classes\$t\OpenWithProgids" $progId ''
    Set-Value "$classes\Applications\PdfDesk.exe\SupportedTypes" $t ''
    Set-Value 'PdfDesk\Capabilities\FileAssociations' $t $progId
}
Set-Value 'RegisteredApplications' 'PdfDesk' 'Software\PdfDesk\Capabilities'

# Keep what an earlier run recorded (a re-run finds its own values in place and records nothing new).
Write-Manifest (@(Read-Manifest) + @($changes))

if ($RegistryRoot -eq 'HKCU:\Software') {
    Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);'
    [Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
}

Write-Host "Installed $pdfExe"
Write-Host "Installed $printExe"
Write-Host "Start Menu: $StartMenu"
Write-Host 'To make PdfDesk the default PDF app: Settings > Apps > Default apps > PdfDesk > .pdf'
