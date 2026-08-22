#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes DemoBrowser as one self-contained win-x64 executable.

.DESCRIPTION
  Recreates the ignored publish\ folder next to this script with a single DemoBrowser.exe
  (runtime bundled; no extra DLLs, PDBs or XML docs). Requires the .NET 10 SDK and,
  to run the result, the Microsoft Edge WebView2 Evergreen Runtime.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'DemoBrowser\DemoBrowser.csproj'
$output = Join-Path $PSScriptRoot 'publish'

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
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:CopyOutputSymbolsToPublishDirectory=false `
    -p:AllowedReferenceRelatedFileExtensions=none `
    --output $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$published = @(Get-ChildItem $output -File)
if ($published.Count -ne 1 -or $published[0].Name -ne 'DemoBrowser.exe') {
    $names = ($published | ForEach-Object { $_.Name }) -join ', '
    throw "Expected a single DemoBrowser.exe; publish produced: $names"
}

Write-Host "Published $($published[0].FullName) ($([math]::Round($published[0].Length / 1MB, 1)) MB)"
