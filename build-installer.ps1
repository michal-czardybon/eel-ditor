# Builds the Eel-ditor installer: publishes a self-contained single-file exe, then compiles the Inno Setup script.
# Output: artifacts\installer\EelDitor-<version>-setup.exe
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish src/EelDitor.App -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:OutputPath=bin/PublishBuild/ -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php" }

& $iscc installer\EelDitor.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Get-ChildItem artifacts\installer\*.exe | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { "Installer: $($_.FullName)" }
