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
    real main window, copies -Session into the data folder as session.json, -Settings as settings.json and -Widths as
    widths.json first, and
    waits for the window's "Window: shown" log line. With -ThemeSettings it starts the theme settings window instead
    (YAMV_DEV_HARNESS=1 and
    YAMV_DEV_THEME_SETTINGS=1), with Studio applied to follow the system, after copying the *.yamv-theme.json files of
    -Themes into the data folder's themes folder, and waits for its "Window: shown" log line. With -ImportFile it also
    opens the Import notice for that file, the harness's picker answering with it once, so the capture shows the notice.
    Its Delete deletes the file outright inside the data folder, and Edit as text writes a "Harness: edit as text <path>"
    log line.
    Both lines are read from the data folder's logs\yamv.log. A fresh WebView2 profile starts
    slowly, hence the wait. With -DeleteAfterShown it then deletes that file and waits for the window's missing-file
    re-check. Then it captures the window with capture-window.ps1, stops yamv.exe, waits for its msedgewebview2 processes
    again, and prints the log.

.PARAMETER ArgLine
    The harness's command line, for example '--theme dark --find'. Default: none.

.PARAMETER App
    Run the real main window instead of the harness, on a fresh data folder under .tmp\yamv-data.

.PARAMETER Session
    With -App: a session.json to start from (its documents and layout), copied into the fresh data folder. Default: none
    (an empty window). A theme to start with goes in -Settings.

.PARAMETER Settings
    With -App: a settings.json to start from, copied into the fresh data folder; seed a theme here, for example
    { "themeId": "ink", "appearance": "dark" }. Default: none (Studio, following the system).

.PARAMETER Widths
    With -App: a widths.json to start from, copied into the fresh data folder; seed stored content widths here, for example
    { "default": 800, "folders": { "C:\\docs": 600 } }. Default: none (every document at the 800 px app default).

.PARAMETER DeleteAfterShown
    With -App: a file to delete once the window has shown, normally the session's selected document, so the capture shows
    the window after it switched from that document to its missing pane. The capture waits 3 s for the 2 s re-check.

.PARAMETER WidthPopover
    With -App: open the content-width popover once the window has shown its first document (YAMV_DEV_WIDTH_POPOVER=1), and
    paint it, a window of its own, over the capture. The run waits for the "Window: width popover shown" log line.

.PARAMETER WidthPopoverStep
    With -WidthPopover: what to do once the popover is open. clearall asks the Clear all question; folder selects the
    document's own folder segment, so the line "Inherited from" shows when the width comes from a folder above it. Default:
    none (the popover as it opens).

.PARAMETER Toasts
    With -App: show three sample toasts once the window has shown (YAMV_DEV_TOASTS=1): a refusal with inline code, a
    failure repeated three times and a plain failure, and paint their popup, a window of its own, over the capture. The
    run waits for the "Window: toasts shown" log line. Not with -WidthPopover.

.PARAMETER ThemeSettingsEntry
    With -App: menu opens the title bar's ⋯ menu once the window has shown its first document and paints the menu, a window
    of its own, over the capture; the run waits for the "Window: more menu shown" log line. open asks for the theme settings
    window as the menu's item does and captures that window instead of the main one; the run waits for the "Window: theme
    settings shown" log line. Passed as YAMV_DEV_THEME_SETTINGS_ENTRY. Not with -WidthPopover or -Toasts.

.PARAMETER ThemeSettings
    Run the theme settings window instead of the harness, on a fresh data folder under .tmp\yamv-data.

.PARAMETER Themes
    With -ThemeSettings: a folder whose *.yamv-theme.json files are copied into the fresh data folder's themes folder
    first. Default: none (only the built-in themes).

.PARAMETER ImportFile
    With -ThemeSettings: a theme file whose Import notice the window opens once it has shown, instead of waiting for a pick.
    Passed to the harness as YAMV_DEV_IMPORT_FILE, which it answers its Import picker with once. Default: none.

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
        -Session .tmp/session.json -Settings .tmp/settings.json -Out .tmp/main.png

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -App `
        -ThemeSettingsEntry menu -Out .tmp/more-menu.png

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -ThemeSettings `
        -Themes .tmp/themes -Out .tmp/theme-settings.png

.EXAMPLE
    pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -ThemeSettings `
        -ImportFile .tmp/warm-solar.yamv-theme.json -Out .tmp/notice.png
#>
[CmdletBinding()]
param(
    [string] $ArgLine = '',
    [switch] $App,
    [string] $Session = '',
    [string] $Settings = '',
    [string] $Widths = '',
    [string] $DeleteAfterShown = '',
    [switch] $WidthPopover,
    [ValidateSet('', 'clearall', 'folder')] [string] $WidthPopoverStep = '',
    [switch] $Toasts,
    [ValidateSet('', 'menu', 'open')] [string] $ThemeSettingsEntry = '',
    [switch] $ThemeSettings,
    [string] $Themes = '',
    [string] $ImportFile = '',
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
if (($Session -or $Settings -or $Widths -or $DeleteAfterShown -or $WidthPopover -or $Toasts -or $ThemeSettingsEntry) -and -not $App) {
    throw ('-Session, -Settings, -Widths, -DeleteAfterShown, -WidthPopover, -Toasts and -ThemeSettingsEntry need -App: ' +
        'the harnesses read no session, settings.json or widths.json.')
}
if ($Toasts -and $WidthPopover) {
    throw '-Toasts and -WidthPopover each wait for their own log line; pass one.'
}
if ($ThemeSettingsEntry -and ($WidthPopover -or $Toasts)) {
    throw '-ThemeSettingsEntry, -WidthPopover and -Toasts each wait for their own log line; pass one.'
}
if ($WidthPopoverStep -and -not $WidthPopover) {
    throw '-WidthPopoverStep needs -WidthPopover: it acts on the popover that switch opens.'
}
if ($App -and $ThemeSettings) {
    throw '-App and -ThemeSettings each start a different window; pass one.'
}
if ($Themes -and -not $ThemeSettings) {
    throw '-Themes needs -ThemeSettings: only the theme settings window reads the themes folder here.'
}
if ($ImportFile -and -not $ThemeSettings) {
    throw '-ImportFile needs -ThemeSettings: only the theme settings window has an Import notice.'
}
$importPath = if ($ImportFile) {
    if (-not (Test-Path -LiteralPath $ImportFile -PathType Leaf)) {
        throw "-ImportFile names no file: $ImportFile"
    }
    (Resolve-Path -LiteralPath $ImportFile).Path
}
else {
    $null
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
    if ($Settings) {
        Copy-Item -LiteralPath $Settings -Destination (Join-Path $dataDir 'settings.json')
    }
    if ($Widths) {
        Copy-Item -LiteralPath $Widths -Destination (Join-Path $dataDir 'widths.json')
    }
    $env:YAMV_DEV_HARNESS = $null
    $env:YAMV_DEV_THEME_SETTINGS = $null
    $env:YAMV_DEV_IMPORT_FILE = $null
    $env:YAMV_DEV_WIDTH_POPOVER = if ($WidthPopoverStep) { $WidthPopoverStep } elseif ($WidthPopover) { '1' } else { $null }
    $env:YAMV_DEV_TOASTS = if ($Toasts) { '1' } else { $null }
    $env:YAMV_DEV_THEME_SETTINGS_ENTRY = if ($ThemeSettingsEntry) { $ThemeSettingsEntry } else { $null }
    $readyLine = if ($WidthPopover) { 'Window: (width popover shown|no width popover to show)' }
    elseif ($Toasts) { 'Window: toasts shown' }
    elseif ($ThemeSettingsEntry -eq 'menu') { 'Window: more menu shown' }
    elseif ($ThemeSettingsEntry -eq 'open') { 'Window: (theme settings shown|no theme settings window to show)' }
    else { 'Window: shown' }
}
elseif ($ThemeSettings) {
    if ($Themes) {
        $themesDir = Join-Path $dataDir 'themes'
        New-Item -ItemType Directory -Path $themesDir | Out-Null
        Get-ChildItem -LiteralPath $Themes -Filter '*.yamv-theme.json' | Copy-Item -Destination $themesDir
    }
    $env:YAMV_DEV_HARNESS = '1'
    $env:YAMV_DEV_THEME_SETTINGS = '1'
    $env:YAMV_DEV_IMPORT_FILE = $importPath
    $env:YAMV_DEV_WIDTH_POPOVER = $null
    $env:YAMV_DEV_TOASTS = $null
    $env:YAMV_DEV_THEME_SETTINGS_ENTRY = $null
    $readyLine = 'Window: shown'
}
else {
    $env:YAMV_DEV_HARNESS = '1'
    $env:YAMV_DEV_THEME_SETTINGS = $null
    $env:YAMV_DEV_IMPORT_FILE = $null
    $env:YAMV_DEV_WIDTH_POPOVER = $null
    $env:YAMV_DEV_TOASTS = $null
    $env:YAMV_DEV_THEME_SETTINGS_ENTRY = $null
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
    if ($ThemeSettingsEntry -eq 'open') {
        & $capture -ProcessId $process.Id -Out $Out -Title 'Theme settings'
    }
    else {
        & $capture -ProcessId $process.Id -Out $Out -WithPopups:($WidthPopover -or $Toasts -or $ThemeSettingsEntry -eq 'menu')
    }
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
