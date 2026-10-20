<#
.SYNOPSIS
    Starts the Release yamv.exe harness, captures its window after a wait, stops it and prints the log lines the run wrote.

.DESCRIPTION
    Run it on a hidden desktop, so no window ever shows on the user's screen:

        pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -Out .tmp/shot.png

    Before it starts yamv.exe it waits for the msedgewebview2 processes of a previous run to exit, since a new run
    that starts beside them shares their WebView2 user data folder. It captures the window with capture-window.ps1,
    stops yamv.exe, waits for its msedgewebview2 processes again, and prints the part of the YAMV log
    (%APPDATA%\YAMV\logs\yamv.log) written since it started.

.PARAMETER ArgLine
    The harness's command line, for example '--theme dark --find'. Default: none.

.PARAMETER Out
    The PNG file to write.

.PARAMETER Wait
    Seconds between starting yamv.exe and the capture. Default 8.

.PARAMETER WebViewTimeoutSeconds
    How long to wait for msedgewebview2 processes to exit, before and after the run. Default 15.

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -ArgLine '--theme dark' -Out .tmp/shot-dark.png
#>
[CmdletBinding()]
param(
    [string] $ArgLine = '',
    [Parameter(Mandatory)] [string] $Out,
    [int] $Wait = 8,
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

if (-not (Test-Path -LiteralPath $exe)) {
    throw "$exe does not exist; build first: dotnet build Yamv.slnx -c Release"
}

Wait-WebViewExit -TimeoutSeconds $WebViewTimeoutSeconds
$start = if (Test-Path -LiteralPath $log) { (Get-Item -LiteralPath $log).Length } else { 0 }
$startArgs = @{ FilePath = $exe; PassThru = $true }
if ($ArgLine) {
    $startArgs.ArgumentList = $ArgLine
}
$process = Start-Process @startArgs
try {
    Start-Sleep -Seconds $Wait
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
$stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
try {
    [void]$stream.Seek($start, 'Begin')
    $reader = New-Object IO.StreamReader $stream
    Write-Output $reader.ReadToEnd()
}
finally {
    $stream.Dispose()
}
