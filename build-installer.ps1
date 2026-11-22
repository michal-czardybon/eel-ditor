# Builds the Eel-ditor installer: publishes a self-contained single-file exe, signs it, then compiles the Inno Setup
# script, which signs the uninstaller and the setup program.
# Output: artifacts\installer\EelDitor-<version>-setup.exe
#
# Signing uses a code-signing certificate from the current user's store (Cert:\CurrentUser\My): the one given by
# -CertThumbprint, else the only valid one with a private key. -NoSign builds unsigned files.
param(
    [string]$CertThumbprint,
    [switch]$NoSign,
    [string]$SignTool = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.17134.0\x64\signtool.exe",
    [string]$TimestampUrl = "http://time.certum.pl"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Find-Certificate {
    $certs = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and (-not $CertThumbprint -or $_.Thumbprint -eq $CertThumbprint) })
    if ($certs.Count -eq 0) { throw "No valid code-signing certificate found in Cert:\CurrentUser\My (use -NoSign to build unsigned)." }
    if ($certs.Count -gt 1) { throw "Several code-signing certificates found; choose one with -CertThumbprint: $($certs.Thumbprint -join ', ')" }
    $certs[0]
}

function Invoke-Sign([string]$file) {
    & $SignTool sign /sha1 $cert.Thumbprint /fd sha256 /tr $TimestampUrl /td sha256 /d "Eel-ditor" $file
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
    & $SignTool verify /pa $file
    if ($LASTEXITCODE -ne 0) { throw "signature verification failed for $file" }
}

if (-not $NoSign) {
    if (-not (Test-Path $SignTool)) { throw "signtool.exe not found: $SignTool (pass -SignTool <path>)" }
    $cert = Find-Certificate
    "Signing with: $($cert.Subject) ($($cert.Thumbprint))"
}

dotnet publish src/EelDitor.App -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:OutputPath="$PSScriptRoot/bin/EelDitor.App/PublishBuild/" -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The program is signed before Inno Setup packs it, so the installed exe carries the signature too.
if (-not $NoSign) { Invoke-Sign publish\win-x64\EelDitor.exe }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php" }

$isccArgs = @()
if (-not $NoSign) {
    # Inno Setup signs the uninstaller and the setup program with this command ($q = quote, $f = the quoted file).
    $isccArgs += '/DSIGN'
    $isccArgs += '/Seelsign=$q' + $SignTool + '$q sign /sha1 ' + $cert.Thumbprint + ' /fd sha256 /tr ' + $TimestampUrl + ' /td sha256 /d Eel-ditor $f'
}
$started = Get-Date
& $iscc @isccArgs installer\EelDitor.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
$setup = Get-ChildItem artifacts\installer\*.exe | Where-Object LastWriteTime -ge $started | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $setup) { throw "ISCC produced no installer in artifacts\installer" }

if (-not $NoSign) {
    & $SignTool verify /pa $setup.FullName
    if ($LASTEXITCODE -ne 0) { throw "signature verification failed for $($setup.FullName)" }
}
"Installer: $($setup.FullName)"
