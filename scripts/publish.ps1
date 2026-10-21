<#
.SYNOPSIS
    Publishes YAMV self-contained for one runtime into publish/<rid>.

.DESCRIPTION
    win-x64 is one self-extracting yamv.exe (native libraries included). osx-arm64 and osx-x64 are a folder, which
    scripts/make-app.sh turns into YAMV.app on a Mac. Trimming is off. Runs from any folder.

.PARAMETER Rid
    The runtime to publish for: win-x64, osx-arm64 or osx-x64.

.EXAMPLE
    pwsh -NoProfile -File scripts/publish.ps1 -Rid win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'osx-arm64', 'osx-x64')] [string] $Rid
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src/Yamv.App/Yamv.App.csproj'
$out = Join-Path $root "publish/$Rid"

if (Test-Path -LiteralPath $out) {
    Remove-Item -LiteralPath $out -Recurse -Force
}

$publishArgs = @('publish', $project, '-c', 'Release', '-r', $Rid, '--self-contained', '-o', $out)
if ($Rid -eq 'win-x64') {
    $publishArgs += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for $Rid failed with exit code $LASTEXITCODE."
}
Write-Output "Published $Rid to $out"
