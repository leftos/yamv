<#
.SYNOPSIS
    Builds the per-user Windows installer from a fresh publish, and optionally installs it.

.DESCRIPTION
    Publishes win-x64, stamps the build with Directory.Build.props' version and the current commit, and compiles
    installer/yamv.iss into dist/YAMV-Setup.exe with Inno Setup 6. With -Install the setup runs without a wizard,
    and a YAMV that was running beforehand is started again with its session restored. Runs from any folder.

.PARAMETER Install
    Installs the built setup silently, replacing an existing install. Settings opens on a first install only.

.EXAMPLE
    pwsh -NoProfile -File scripts/make-installer.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/make-installer.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Test-YamvRunning([string] $ExePath) {
    # Path is null for a process this account cannot read, which is never the per-user install.
    foreach ($process in @(Get-Process -Name yamv -ErrorAction SilentlyContinue)) {
        $path = $process.Path
        if ($path -and ($path -ieq $ExePath)) {
            return $true
        }
    }

    return $false
}

function Start-Yamv([string] $ExePath) {
    # Explorer starts YAMV at normal priority, outside this script's process tree.
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "`"$ExePath`""
}

& pwsh -NoProfile -File (Join-Path $root 'scripts/publish.ps1') -Rid win-x64
if ($LASTEXITCODE -ne 0) {
    throw "publish.ps1 failed with exit code $LASTEXITCODE."
}

$props = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props'))
$numeric = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$sha = git -C $root rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) {
    throw "git rev-parse failed with exit code $LASTEXITCODE."
}
if (git -C $root status --porcelain) {
    $sha = "$sha-dirty"
}

$appVersion = "$numeric+$sha"

$candidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

$isccArgs = @(
    '/Q'
    "/DAppVersion=$appVersion"
    "/DNumericVersion=$numeric"
    "/DSourceExe=$root\publish\win-x64\yamv.exe"
    "/DOutputDir=$root\dist"
    (Join-Path $root 'installer/yamv.iss')
)
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE."
}

Write-Output "Built dist\YAMV-Setup.exe (YAMV $appVersion)"

if (-not $Install) {
    return
}

$installed = Join-Path $env:LOCALAPPDATA 'Programs\YAMV\yamv.exe'
$wasRunning = Test-YamvRunning -ExePath $installed

New-Item -ItemType Directory -Force (Join-Path $root '.tmp') | Out-Null
$log = Join-Path $root '.tmp\yamv-setup.log'
$setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"")
try {
    $setup = Start-Process -FilePath "$root\dist\YAMV-Setup.exe" -ArgumentList $setupArgs -Wait -PassThru
    if ($setup.ExitCode -eq 8) {
        throw "Setup installed YAMV but yamv --register failed; YAMV is not registered for .md and .markdown. Its log is $log."
    }
    if ($setup.ExitCode -ne 0) {
        throw "Setup failed with exit code $($setup.ExitCode); YAMV was not updated. Its log is $log."
    }
}
finally {
    if ($wasRunning -and -not (Test-YamvRunning -ExePath $installed)) {
        Start-Yamv $installed
        Write-Output 'Restarted YAMV.'
    }
}

Write-Output "Installed YAMV $appVersion to $(Split-Path -Parent $installed)."
