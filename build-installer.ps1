# Builds the Eel-ditor installer: publishes a self-contained single-file exe, signs it, then compiles the Inno Setup
# script, which signs the uninstaller and the setup program.
# Output: artifacts\installer\EelDitor-<version>-setup.exe
# Needs: the .NET SDK, Inno Setup 6, and the Visual Studio x64 C++ build tools with a Windows SDK.
# Signing also needs signtool.exe and a code-signing certificate.
#
# Signing is optional. By default the files are signed when signtool.exe and a certificate are found, otherwise the
# script says why and builds unsigned files. -Sign makes signing required (it stops before building when it cannot
# sign: use it for releases); -NoSign never signs.
# The certificate comes from the current user's store (Cert:\CurrentUser\My): the one given by -CertThumbprint, else
# the only valid one with a private key. signtool.exe is taken from -SignTool, else PATH, else the newest Windows SDK.
param(
    [string]$CertThumbprint,
    [switch]$Sign,
    [switch]$NoSign,
    [string]$SignTool,
    [string]$TimestampUrl = "http://time.certum.pl"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ($Sign -and $NoSign) { throw "-Sign and -NoSign cannot be used together." }

function Find-SignTool {
    if ($SignTool) { return (Test-Path $SignTool) ? $SignTool : $null }
    $onPath = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
    if ($onPath) { return $onPath }
    $arch = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' ? 'arm64' : 'x64'
    Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\$arch\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Directory.Parent.Name -replace '[^0-9.]', '') } -ErrorAction SilentlyContinue |
        Select-Object -Last 1 -ExpandProperty FullName
}

function Invoke-Sign([string]$file) {
    & $SignTool sign /sha1 $cert.Thumbprint /fd sha256 /tr $TimestampUrl /td sha256 /d "Eel-ditor" $file
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
    & $SignTool verify /pa $file
    if ($LASTEXITCODE -ne 0) { throw "signature verification failed for $file" }
}

# Decide before building whether the files will be signed, so -Sign fails at once and nobody waits for a build
# that cannot be signed.
$signing = $false
if (-not $NoSign) {
    $why = $null
    $tool = Find-SignTool
    if (-not $tool) {
        $why = $SignTool ? "signtool.exe not found at $SignTool" : "signtool.exe not found (it comes with the Windows SDK; or pass -SignTool <path>)"
    }
    else {
        $SignTool = $tool
        $certs = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and (-not $CertThumbprint -or $_.Thumbprint -eq $CertThumbprint) })
        if ($certs.Count -eq 0) {
            $why = $CertThumbprint ? "no valid code-signing certificate with thumbprint $CertThumbprint in Cert:\CurrentUser\My" : "no valid code-signing certificate in Cert:\CurrentUser\My"
        }
        elseif ($certs.Count -gt 1) { $why = "several code-signing certificates; choose one with -CertThumbprint: $($certs.Thumbprint -join ', ')" }
        else { $cert = $certs[0] }
    }
    if ($why) {
        if ($Sign) { throw "Cannot sign: $why." }
        Write-Warning "Building UNSIGNED files: $why. (-NoSign skips signing without this warning; -Sign makes it an error.)"
    }
    else {
        $signing = $true
        "Signing with: $($cert.Subject) ($($cert.Thumbprint)), $SignTool"
    }
}

dotnet publish src/EelDitor.App -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:OutputPath="$PSScriptRoot/bin/EelDitor.App/PublishBuild/" -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$vcvars = $null
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vcvars = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat' | Select-Object -First 1
    }
    if (-not $vcvars) { throw "Visual Studio x64 C++ build tools with a Windows SDK are required for the Explorer menu." }
}
& installer\windows\build-context-menu.cmd $vcvars
if ($LASTEXITCODE -ne 0) { throw "Building the Explorer menu extension failed." }

if ($signing) {
    Invoke-Sign publish\win-x64\EelDitor.exe
    Invoke-Sign publish\win-x64\EelDitorGitMenu.dll
}

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php" }

$isccArgs = @()
if ($signing) {
    # Inno Setup signs the uninstaller and the setup program with this command ($q = quote, $f = the quoted file).
    $isccArgs += '/DSIGN'
    $isccArgs += '/Seelsign=$q' + $SignTool + '$q sign /sha1 ' + $cert.Thumbprint + ' /fd sha256 /tr ' + $TimestampUrl + ' /td sha256 /d Eel-ditor $f'
}
$started = Get-Date
& $iscc @isccArgs installer\EelDitor.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
$setup = Get-ChildItem artifacts\installer\*.exe | Where-Object LastWriteTime -ge $started | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $setup) { throw "ISCC produced no installer in artifacts\installer" }

if ($signing) {
    & $SignTool verify /pa $setup.FullName
    if ($LASTEXITCODE -ne 0) { throw "signature verification failed for $($setup.FullName)" }
}
"Installer: $($setup.FullName)" + ($signing ? " (signed)" : " (UNSIGNED)")
