<#
.SYNOPSIS
    Starts the Release yamv.exe (the dev harness, with -App the real main window, or with -ThemeSettings the theme
    settings window), captures its window after a wait,
    stops it and prints the log lines the run wrote.

.DESCRIPTION
    Run it on a hidden desktop, so no window ever shows on the user's screen:

        pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -Out .tmp/shot.png

    Before it starts yamv.exe it waits for the msedgewebview2 processes of a previous run to exit. Each run gets a
    fresh WebView2 user data folder, .tmp\webview2\<run stamp> under the repo, through WEBVIEW2_USER_DATA_FOLDER, so a
    hidden run never joins the browser of a YAMV running on the owner's desktop.

    Every run gets a fresh data folder, .tmp\yamv-data\<run stamp> under the repo, through YAMV_DATA_DIR (any inherited
    value is replaced), so the owner's session, themes and log in %APPDATA%\YAMV are never read or written. By default it
    starts the dev harness (YAMV_DEV_HARNESS=1) and waits for its "Harness: showing" log line. With -App it starts the
    real main window, copies -Session into the data folder as session.json first, and waits for the window's
    "Window: shown" log line. With -ThemeSettings it starts the theme settings window instead (YAMV_DEV_HARNESS=1 and
    YAMV_DEV_THEME_SETTINGS=1), with Studio applied to follow the system, after copying the *.yamv-theme.json files of
    -Themes into the data folder's themes folder, and waits for its "Window: shown" log line. Its Delete deletes the file
    outright inside the data folder, and Edit as text writes a "Harness: edit as text <path>" log line.
    Both lines are read from the data folder's logs\yamv.log. A fresh WebView2 profile starts
    slowly, hence the wait. With -DeleteAfterShown it then deletes that file and waits for the window's missing-file
    re-check. Then it captures the window with capture-window.ps1, stops yamv.exe, waits for its msedgewebview2 processes
    again, and prints the log.

.PARAMETER ArgLine
    The harness's command line, for example '--theme dark --find'. Default: none.

.PARAMETER App
    Run the real main window instead of the harness, on a fresh data folder under .tmp\yamv-data.

.PARAMETER Session
    With -App: a session.json to start from, copied into the fresh data folder. Default: none (an empty window).

.PARAMETER DeleteAfterShown
    With -App: a file to delete once the window has shown, normally the session's selected document, so the capture shows
    the window after it switched from that document to its missing pane. The capture waits 3 s for the 2 s re-check.

.PARAMETER ThemeSettings
    Run the theme settings window instead of the harness, on a fresh data folder under .tmp\yamv-data.

.PARAMETER Themes
    With -ThemeSettings: a folder whose *.yamv-theme.json files are copied into the fresh data folder's themes folder
    first. Default: none (only the built-in themes).

.PARAMETER Out
    The PNG file to write.

.PARAMETER Wait
    The most seconds to wait for the "Harness: showing" (with -App, "Window: shown") log line before capturing
    anyway. Default 30.

.PARAMETER WebViewTimeoutSeconds
    How long to wait for msedgewebview2 processes to exit, before and after the run. Default 15.

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -ArgLine '--theme dark' -Out .tmp/shot-dark.png

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -App `
        -Session .tmp/session.json -Out .tmp/main.png

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -ThemeSettings `
        -Themes .tmp/themes -Out .tmp/theme-settings.png
#>
[CmdletBinding()]
param(
    [string] $ArgLine = '',
    [switch] $App,
    [string] $Session = '',
    [string] $DeleteAfterShown = '',
    [switch] $ThemeSettings,
    [string] $Themes = '',
    [Parameter(Mandatory)] [string] $Out,
    [int] $Wait = 30,
    [int] $WebViewTimeoutSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $root 'src\Yamv.App\bin\Release\net10.0\yamv.exe'
$capture = Join-Path $PSScriptRoot 'capture-window.ps1'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
if (($Session -or $DeleteAfterShown) -and -not $App) {
    throw '-Session and -DeleteAfterShown need -App: the harness does not read a session.'
}
if ($App -and $ThemeSettings) {
    throw '-App and -ThemeSettings each start a different window; pass one.'
}
if ($Themes -and -not $ThemeSettings) {
    throw '-Themes needs -ThemeSettings: only the theme settings window reads the themes folder here.'
}
$dataDir = Join-Path $root ".tmp\yamv-data\$stamp"
New-Item -ItemType Directory -Path $dataDir | Out-Null
$env:YAMV_DATA_DIR = $dataDir
$log = Join-Path $dataDir 'logs\yamv.log'
Write-Output "Data folder: $dataDir"
if ($App) {
    if ($Session) {
        Copy-Item -LiteralPath $Session -Destination (Join-Path $dataDir 'session.json')
    }
    $env:YAMV_DEV_HARNESS = $null
    $env:YAMV_DEV_THEME_SETTINGS = $null
    $readyLine = 'Window: shown'
}
elseif ($ThemeSettings) {
    if ($Themes) {
        $themesDir = Join-Path $dataDir 'themes'
        New-Item -ItemType Directory -Path $themesDir | Out-Null
        Get-ChildItem -LiteralPath $Themes -Filter '*.yamv-theme.json' | Copy-Item -Destination $themesDir
    }
    $env:YAMV_DEV_HARNESS = '1'
    $env:YAMV_DEV_THEME_SETTINGS = '1'
    $readyLine = 'Window: shown'
}
else {
    $env:YAMV_DEV_HARNESS = '1'
    $env:YAMV_DEV_THEME_SETTINGS = $null
    $readyLine = 'Harness: showing'
}

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

function Wait-Ready {
    param([long] $Offset, [System.Diagnostics.Process] $Process, [int] $TimeoutSeconds)

    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $until -and -not $Process.HasExited) {
        if ((Read-LogSince -Offset $Offset) -match $readyLine) {
            Start-Sleep -Seconds 1
            return
        }
        Start-Sleep -Milliseconds 300
    }
    Write-Warning "No '$readyLine' log line after $TimeoutSeconds s; capturing anyway."
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
$userData = Join-Path $root ".tmp\webview2\$stamp"
New-Item -ItemType Directory -Path $userData | Out-Null
$env:WEBVIEW2_USER_DATA_FOLDER = $userData
Write-Output "WebView2 user data folder: $userData"
$process = Start-Process @startArgs
try {
    Wait-Ready -Offset $start -Process $process -TimeoutSeconds $Wait
    if ($DeleteAfterShown) {
        Remove-Item -LiteralPath $DeleteAfterShown
        Write-Output "Deleted $DeleteAfterShown; waiting for the missing-file re-check."
        Start-Sleep -Seconds 3
    }
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
