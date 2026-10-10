# Build a probe with MSVC 2022 without relying on vcvars64.bat
# (vcvars64.bat is broken in this environment: it aborts with a vswhere error).
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$ExeName = "",
    [string[]]$Libs = @("user32.lib", "gdi32.lib", "shcore.lib"),
    [switch]$Console = $true
)
$ErrorActionPreference = "Stop"

$msvcRoot = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC"
$msvcVer = (Get-ChildItem $msvcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$msvc = Join-Path $msvcRoot $msvcVer
$sdkRoot = "C:\Program Files (x86)\Windows Kits\10"
$sdkVer = (Get-ChildItem (Join-Path $sdkRoot "Include") -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkInc = Join-Path $sdkRoot "Include\$sdkVer"
$sdkLib = Join-Path $sdkRoot "Lib\$sdkVer"
$sdkBin = Join-Path $sdkRoot "bin\$sdkVer\x64"
$msvcBin = Join-Path $msvc "bin\Hostx64\x64"

Write-Host "MSVC $msvcVer / SDK $sdkVer"

$env:INCLUDE = "$msvc\include;$sdkInc\ucrt;$sdkInc\shared;$sdkInc\um;$sdkInc\winrt"
$env:LIB = "$msvc\lib\x64;$sdkLib\ucrt\x64;$sdkLib\um\x64"
$env:PATH = "$msvcBin;$sdkBin;$env:PATH"

$dir = "C:\ai-projects\cursor-trap\probe"
$buildDir = Join-Path $dir "build"
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dir "out") | Out-Null

$src = (Resolve-Path $Source).Path
if (-not $ExeName) { $ExeName = [System.IO.Path]::GetFileNameWithoutExtension($src) }
$libArgs = ($Libs) -join " "

$cl = Join-Path $msvcBin "cl.exe"
$args = @("/nologo", "/W3", "/EHsc", "/std:c++17", "/Zi", "/MD",
    "/Fo$buildDir\", "/Fe$buildDir\$ExeName.exe", $src,
    "/link", "/SUBSYSTEM:CONSOLE", "/INCREMENTAL:NO")
$args += $Libs

Write-Host "== building $ExeName =="
& $cl @args
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }
Write-Host "== built: $buildDir\$ExeName.exe =="
