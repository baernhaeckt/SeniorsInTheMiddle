#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes DemoBrowser as one self-contained single-file win-x64 executable: publish\win-x64\DemoBrowser.exe.

.DESCRIPTION
  Recreates the ignored publish\win-x64\ folder next to this script. The result is a single .exe and nothing
  else: it carries the .NET runtime, the app, the bundled Chromium (CEF) runtime and the CefGlueBrowserProcess
  helper. Nothing has to be installed on the target machine (in particular no WebView2 runtime — this build
  brings its own Chromium). Requires the .NET 10 SDK.

  HOW the single file works: the apphost extracts the whole payload once into
  %TEMP%\.net\DemoBrowser\<hash>\ and runs from there, so libcef.dll finds icudtl.dat, *.pak and locales\
  next to itself and CEF can start the helper as a real child process. The extraction is keyed by content
  hash, so it happens on first launch only and is reused afterwards. This needs IncludeAllContentForSelfExtract
  (the default single-file bundler embeds managed assemblies only and would leave Chromium's data files behind).

.PARAMETER Compress
  Compress the payload inside the .exe (roughly halves the file, at the cost of a noticeably slower first
  launch while it is extracted). Off by default.

.PARAMETER Zip
  Also pack the .exe into publish\DemoBrowser-win-x64.zip.
#>
[CmdletBinding()]
param(
    [switch] $Compress,
    [switch] $Zip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'DemoBrowser\DemoBrowser.csproj'
$output = Join-Path $PSScriptRoot 'publish'
$exe = Join-Path $output 'DemoBrowser.exe'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not on PATH. Install .NET 10 from https://dotnet.microsoft.com/download'
}

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$($Compress.IsPresent.ToString().ToLowerInvariant()) `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:CopyOutputSymbolsToPublishDirectory=false `
    -p:AllowedReferenceRelatedFileExtensions=none `
    --output $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $exe)) {
    throw "Publish did not produce $exe."
}

# The point of this script: one file, nothing beside it.
$strays = Get-ChildItem $output -Force | Where-Object { $_.Name -ne 'DemoBrowser.exe' }
if ($strays) {
    throw "Publish output is not a single file; also present: $(($strays | ForEach-Object Name) -join ', ')"
}

# Everything Chromium needs at runtime, verified against the bundle manifest at the end of the .exe (it lists
# every embedded file by its relative path, with '/' separators). A missing piece means a blank window instead
# of a browser, and finding that out here beats finding it out on the demo machine.
$required = @(
    'libcef.dll',
    'icudtl.dat',
    'chrome_elf.dll',
    'v8_context_snapshot.bin',
    'resources.pak',
    'locales/en-US.pak',
    'CefGlueBrowserProcess/Xilium.CefGlue.BrowserProcess.exe'
)

# The manifest sits in the last few KB; read a generous tail rather than the whole ~500 MB file.
$stream = [System.IO.File]::OpenRead($exe)
try {
    $tailLength = [math]::Min(1MB, $stream.Length)
    $stream.Seek(-$tailLength, [System.IO.SeekOrigin]::End) | Out-Null
    $buffer = New-Object byte[] $tailLength
    $read = 0
    while ($read -lt $tailLength) {
        $chunk = $stream.Read($buffer, $read, $tailLength - $read)
        if ($chunk -le 0) { break }
        $read += $chunk
    }
    $manifest = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
}
finally {
    $stream.Dispose()
}

$missing = $required | Where-Object { $manifest -notlike "*$_*" }
if ($missing) {
    throw "The bundle is incomplete; missing: $($missing -join ', ')"
}

$sizeMb = [math]::Round(((Get-Item $exe).Length / 1MB), 1)
Write-Host "Published $exe ($sizeMb MB)"
Write-Host 'Hand out that one file; it unpacks itself into %TEMP%\.net\DemoBrowser on first launch.'

if ($Zip) {
    $archive = Join-Path $PSScriptRoot 'publish\DemoBrowser-win-x64.zip'
    if (Test-Path $archive) {
        Remove-Item $archive -Force
    }

    Compress-Archive -Path $exe -DestinationPath $archive
    $zipMb = [math]::Round(((Get-Item $archive).Length / 1MB), 1)
    Write-Host "Packed $archive ($zipMb MB)"
}
