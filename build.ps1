# Builds GDLoLControls.exe (C# compiler that ships with Windows) and GDLoLHook.dll (Visual Studio C++ Build Tools, if installed).
#   .\build.ps1                 -> dist\GDLoLControls.exe, dist\GDLoLHook.dll
#   .\build.ps1 -Tests          -> also tests\bin\UnitTests.exe
#   .\build.ps1 -Out <folder>   -> build into another folder
param([switch]$Tests, [string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $Out | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc (.NET Framework 4.8 is part of Windows 10/11)" }
$common = @('/nologo', '/optimize+', '/debug:pdbonly', '/platform:x64', '/warn:4', '/nowarn:1591',
            '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
$manifest = "/win32manifest:$(Join-Path $root 'app.manifest')"

function Invoke-Csc([string]$what, [string[]]$cscArgs) {
    $log = & $csc @cscArgs | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "csc failed for $what" }
    ($log -split "`r?`n") | Where-Object { $_ -match 'warning CS' } | ForEach-Object { Write-Host $_ }
    Write-Host "built $what"
}

# app icon, drawn here so the repository needs no binary assets
$ico = Join-Path $root 'obj\app.ico'
if (-not (Test-Path $ico)) {
    New-Item -ItemType Directory -Force (Split-Path $ico) | Out-Null
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 40, 120, 70))), 1, 1, 30, 30)
    $f = New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('GD', $f, [System.Drawing.Brushes]::White, 4, 8)
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $fs = [System.IO.File]::Create($ico); $icon.Save($fs); $fs.Close()
    $g.Dispose(); $bmp.Dispose()
}

$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $Out 'GDLoLControls.exe'
Invoke-Csc 'GDLoLControls.exe' (@('/target:winexe', "/out:$exe", $manifest, "/win32icon:$ico") + $common + $src)
Write-Host ("GDLoLControls.exe SHA-256 " + (Get-FileHash $exe -Algorithm SHA256).Hash)

# the helper loaded into the game (optional: without it the app reads the screen instead)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = $null
if (Test-Path $vswhere) { $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1 }
$vcvars = if ($vs) { Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat' } else { $null }
if ($vcvars -and (Test-Path $vcvars)) {
    $obj = Join-Path $root 'obj'
    New-Item -ItemType Directory -Force $obj | Out-Null
    $dll = Join-Path $Out 'GDLoLHook.dll'
    $cl = "`"$vcvars`" >nul && cl /nologo /O2 /MT /W4 /wd4100 /wd4702 /GS /EHs-c- /LD /Fo`"$obj\\`" `"$(Join-Path $root 'hook\GDLoLHook.cpp')`" /link /OUT:`"$dll`" /DLL /NOLOGO /DYNAMICBASE /NXCOMPAT kernel32.lib user32.lib"
    $ErrorActionPreference = 'Continue'                 # vcvars64 writes harmless messages to stderr
    $log = cmd /c "$cl 2>&1" | Out-String
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "GDLoLHook.dll build failed" }
    Remove-Item (Join-Path $Out 'GDLoLHook.exp'), (Join-Path $Out 'GDLoLHook.lib') -ErrorAction SilentlyContinue
    Write-Host "built GDLoLHook.dll"
    Write-Host ("GDLoLHook.dll SHA-256 " + (Get-FileHash $dll -Algorithm SHA256).Hash)
} else {
    Write-Host "GDLoLHook.dll skipped: Visual Studio C++ Build Tools not found (the app then reads the screen instead of the game)"
}

if ($Tests) {
    $bin = Join-Path $root 'tests\bin'
    New-Item -ItemType Directory -Force $bin | Out-Null
    $srcNoMain = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | Where-Object { $_.Name -ne 'Program.cs' } | ForEach-Object { $_.FullName }
    $t = Join-Path $root 'tests'
    $unit = @((Join-Path $t 'MiniTest.cs'), (Join-Path $t 'UnitMain.cs'), (Join-Path $t 'SimWorld.cs'))
    $unit += Get-ChildItem $t -Filter *Tests.cs | ForEach-Object { $_.FullName }
    Invoke-Csc 'UnitTests.exe' (@('/target:exe', "/out:$(Join-Path $bin 'UnitTests.exe')", $manifest) + $common + $srcNoMain + $unit)
}
