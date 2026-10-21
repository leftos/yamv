<#
.SYNOPSIS
    Starts the Release yamv.exe harness, captures its window after a wait, stops it and prints the log lines the run wrote.

.DESCRIPTION
    Run it on a hidden desktop, so no window ever shows on the user's screen:

        pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -Out .tmp/shot.png

    Before it starts yamv.exe it waits for the msedgewebview2 processes of a previous run to exit. Each run gets a
    fresh WebView2 user data folder, .tmp\webview2\<run stamp> under the repo, through WEBVIEW2_USER_DATA_FOLDER, so a
    hidden run never joins the browser of a YAMV running on the owner's desktop. It waits for the harness's
    "Harness: showing" log line (a fresh profile starts slowly), captures the window with capture-window.ps1, stops
    yamv.exe, waits for its msedgewebview2 processes again, and prints the part of the YAMV log
    (%APPDATA%\YAMV\logs\yamv.log) written since it started.

.PARAMETER ArgLine
    The harness's command line, for example '--theme dark --find'. Default: none.

.PARAMETER Out
    The PNG file to write.

.PARAMETER Wait
    The most seconds to wait for the "Harness: showing" log line before capturing anyway. Default 30.

.PARAMETER WebViewTimeoutSeconds
    How long to wait for msedgewebview2 processes to exit, before and after the run. Default 15.

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -ArgLine '--theme dark' -Out .tmp/shot-dark.png
#>
[CmdletBinding()]
param(
    [string] $ArgLine = '',
    [Parameter(Mandatory)] [string] $Out,
    [int] $Wait = 30,
    [int] $WebViewTimeoutSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $root 'src\Yamv.App\bin\Release\net10.0\yamv.exe'
$capture = Join-Path $PSScriptRoot 'capture-window.ps1'
$log = Join-Path $env:APPDATA 'YAMV\logs\yamv.log'

function Wait-WebViewExit {
    param([int] $TimeoutSeconds)

    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $until) {
        $running = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -match 'yamv' })
        if ($running.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 300
    }
    Write-Warning "msedgewebview2 processes of a YAMV run were still running after $TimeoutSeconds s."
}

function Read-LogSince {
    param([long] $Offset)

    if (-not (Test-Path -LiteralPath $log)) {
        return ''
    }
    $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($Offset, 'Begin')
        $reader = New-Object IO.StreamReader $stream
        return $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

function Wait-HarnessShowing {
    param([long] $Offset, [System.Diagnostics.Process] $Process, [int] $TimeoutSeconds)

    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $until -and -not $Process.HasExited) {
        if ((Read-LogSince -Offset $Offset) -match 'Harness: showing') {
            Start-Sleep -Seconds 1
            return
        }
        Start-Sleep -Milliseconds 300
    }
    Write-Warning "No 'Harness: showing' log line after $TimeoutSeconds s; capturing anyway."
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "$exe does not exist; build first: dotnet build Yamv.slnx -c Release"
}

Wait-WebViewExit -TimeoutSeconds $WebViewTimeoutSeconds
$start = if (Test-Path -LiteralPath $log) { (Get-Item -LiteralPath $log).Length } else { 0 }
$startArgs = @{ FilePath = $exe; PassThru = $true }
if ($ArgLine) {
    $startArgs.ArgumentList = $ArgLine
}
$userData = Join-Path $root ".tmp\webview2\$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')"
New-Item -ItemType Directory -Path $userData | Out-Null
$env:WEBVIEW2_USER_DATA_FOLDER = $userData
Write-Output "WebView2 user data folder: $userData"
$process = Start-Process @startArgs
try {
    Wait-HarnessShowing -Offset $start -Process $process -TimeoutSeconds $Wait
    & $capture -ProcessId $process.Id -Out $Out
    Write-Output "pid=$($process.Id) exited=$($process.HasExited)"
}
finally {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    Wait-WebViewExit -TimeoutSeconds $WebViewTimeoutSeconds
}

if (-not (Test-Path -LiteralPath $log)) {
    throw "The run wrote no log at $log."
}
Write-Output (Read-LogSince -Offset $start)
