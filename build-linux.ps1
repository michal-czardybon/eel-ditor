# Builds the Linux package of Eel-ditor on Windows (or anywhere with PowerShell 7 and the .NET SDK):
# a self-contained single-file executable for linux-x64 (or -Runtime linux-arm64), with an icon, a menu entry and
# an install script, in artifacts\linux\EelDitor-<version>-<runtime>.tar.gz.
# The archive is written with .NET's TarWriter, so the executable and install.sh keep their execute permission
# (Windows' tar cannot set it).
param(
    [ValidateSet('linux-x64', 'linux-arm64')]
    [string]$Runtime = 'linux-x64'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = (dotnet msbuild src/EelDitor.App -getProperty:Version).Trim()
if (-not $version) { throw "Could not read the version (Directory.Build.props)." }

$publish = "publish/$Runtime"
dotnet publish src/EelDitor.App -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:OutputPath="$PSScriptRoot/bin/EelDitor.App/PublishBuild-$Runtime/" -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
$exe = Join-Path $publish 'EelDitor'
if (-not (Test-Path $exe)) { throw "The Linux executable is missing: $exe" }

$name = "EelDitor-$version-$Runtime"
$root = "eel-ditor-$version"
New-Item -ItemType Directory -Force artifacts/linux | Out-Null
$archive = Join-Path $PSScriptRoot "artifacts/linux/$name.tar.gz"

# Files of the package: (path in the archive, source, executable, text with LF line endings).
$files = @(
    @("$root/eel-ditor", $exe, $true, $false),
    @("$root/install.sh", 'installer/linux/install.sh', $true, $true),
    @("$root/eel-ditor.desktop", 'installer/linux/eel-ditor.desktop', $false, $true),
    @("$root/README.txt", 'installer/linux/README.txt', $false, $true),
    @("$root/eel-ditor.png", 'src/EelDitor.App/Assets/app-256.png', $false, $false)
)

$executable = [System.IO.UnixFileMode]493   # rwxr-xr-x
$regular = [System.IO.UnixFileMode]420      # rw-r--r--
$stream = [System.IO.File]::Create($archive)
try {
    $gzip = [System.IO.Compression.GZipStream]::new($stream, [System.IO.Compression.CompressionLevel]::Optimal)
    $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
    try {
        $dir = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$root/")
        $dir.Mode = $executable
        $tar.WriteEntry($dir)
        foreach ($f in $files) {
            $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $f[0])
            $entry.Mode = $f[2] ? $executable : $regular
            $entry.ModificationTime = [DateTimeOffset]::Now
            if ($f[3]) {
                # Scripts and the desktop entry with LF only, whatever the checkout did.
                $text = [System.IO.File]::ReadAllText((Resolve-Path $f[1])).Replace("`r`n", "`n")
                $entry.DataStream = [System.IO.MemoryStream]::new([System.Text.UTF8Encoding]::new($false).GetBytes($text))
            }
            else { $entry.DataStream = [System.IO.File]::OpenRead((Resolve-Path $f[1])) }
            $tar.WriteEntry($entry)
            $entry.DataStream.Dispose()
        }
    }
    finally { $tar.Dispose(); $gzip.Dispose() }
}
finally { $stream.Dispose() }

"Linux package: $archive ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)"
