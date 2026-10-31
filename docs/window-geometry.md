# Window geometry units

What unit each Avalonia window and screen value is in, on Windows and on macOS, and how YAMV converts between them when it saves and restores the main window. Checked against Avalonia 12.1.4 (the version `src/Yamv.App/Yamv.App.csproj` pins); `AV/` below is `https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/`. Read this before changing where the main window reopens.

## Units

| Value | Windows (Avalonia.Win32) | macOS (Avalonia.Native) |
|---|---|---|
| `Window.Position` (`PixelPoint`) | Physical pixels in the virtual screen: the outer window rectangle's top-left (`GetWindowRect`, so it includes the invisible resize borders). Setting it re-reads the target monitor's DPI. `AV/src/Windows/Avalonia.Win32/WindowImpl.cs#L514-L545` | **Points**, despite the `PixelPoint` type: the `NSWindow` frame's top-left, Y flipped against the primary screen's height; no backing-scale factor is applied. `AV/native/Avalonia.Native/src/OSX/WindowBaseImpl.mm#L396-L432`, `AV/src/Avalonia.Native/WindowImplBase.cs#L19-L23` |
| `Screen.Bounds`, `Screen.WorkingArea` (`PixelRect`) | Physical pixels (`rcMonitor`, `rcWork`). `AV/src/Windows/Avalonia.Win32/WinScreen.cs#L22-L23` | Points (`NSScreen.frame`, `visibleFrame`), Y flipped. `AV/native/Avalonia.Native/src/OSX/Screens.mm#L60-L68` |
| `Screen.Scaling` | The monitor's effective DPI / 96. `WinScreen.cs#L24`, `#L97-L118` | Always 1. `Screens.mm#L70` |
| `ClientSize`, `Width`, `Height` | DIPs: the client rectangle / `RenderScaling`. `WindowImpl.cs#L258-L266` | Points (the same as DIPs). `AV/src/Avalonia.Native/TopLevelImpl.cs#L121-L134` |
| `FrameSize` | DIPs of the outer rectangle: title bar, borders and the invisible resize borders. `WindowImpl.cs#L268-L277` | Points of the `NSWindow` frame, title bar included; **null until the window has been shown**. `WindowBaseImpl.mm#L264-L275`, `WindowImplBase.cs#L30-L45` |
| `RenderScaling` | Monitor DPI / 96, updated on `WM_DPICHANGED` and on every `Position` set. `WindowImpl.cs#L254` | The backing scale factor: 2 on a Retina screen. `TopLevelImpl.cs#L99`, `#L136` |
| `DesktopScaling` | Equal to `RenderScaling`. `WindowImpl.cs#L256` | Always 1. `TopLevelImpl.cs#L116` |

`Window.DesktopScaling` is `DesktopScalingOverride ?? PlatformImpl.DesktopScaling ?? 1` (`AV/src/Avalonia.Controls/WindowBase.cs#L135`). Before the first show with `WindowStartupLocation.Manual`, the override is the scaling of the screen under `Position` (`AV/src/Avalonia.Controls/Window.cs#L1271-L1272`); it is cleared after the show (`#L1058`).

## Converting

- Physical position = DIPs × `DesktopScaling`, which is the `Screen.Scaling` of the window's screen. On Windows that is the monitor's DPI / 96; on macOS it is 1, because positions, screens and sizes are all in points.
- `RenderScaling` never converts geometry. It equals `DesktopScaling` on Windows, but on macOS it is the backing scale, so using it for `Position` or `WorkingArea` is wrong by 2× on a Retina screen.
- Avalonia sizes its own startup rectangle the same way: `PixelSize.FromSize(FrameSize ?? ClientSize, DesktopScaling)` (`Window.cs#L1304-L1314`).
- On macOS, Y runs top-down from the primary screen's top, so a screen above the primary has negative Y; `Position` and the screen rectangles agree on it.

## Several monitors

- Windows with mixed DPI: each monitor has its own DIP space, so positions compare only in physical pixels. A move across monitors sends `WM_DPICHANGED`; Avalonia updates its scaling and applies the rectangle Windows suggests, so `ClientSize` in DIPs stays about the same while the pixel size changes (`AV/src/Windows/Avalonia.Win32/WindowImpl.AppWndProc.cs#L192-L213`).
- Set `Position` before `Width` and `Height`: setting `Position` re-reads the DPI of the monitor nearest the new position, so the size that follows is read at the target monitor's DPI. If the platform scaling still disagrees after the show, Avalonia moves the window again (`Window.cs#L1354-L1355`).
- A minimized window's `Position` on Windows is off-screen and is no restore point; YAMV saves bounds only while the window is in the normal state.
- macOS has one point space across all screens, so screens with different backing scales need no conversion.

## YAMV's path

- `src/Yamv.Core/Session/WindowBounds.cs` holds `X`, `Y`, `Width`, `Height` in DIPs of the screen it was measured on, and that screen's `Scaling`: `X * Scaling` is the physical position.
- Save: `MainWindow.UpdateBounds` (`src/Yamv.App/Views/MainWindow.axaml.cs`), on a position, size or state change, only in the normal state: the scaling is `Screens.ScreenFromWindow(this)?.Scaling`, falling back to `DesktopScaling` (the same unit); `X` and `Y` are `Position` divided by it, `Width` and `Height` are `ClientSize`.
- Restore: `MainWindowFactory.RestoreLayout` builds one `ScreenArea` per screen from `WorkingArea` and `Scaling`, and `WindowPlacement.Fit` (`src/Yamv.Core/Session/WindowPlacement.cs`) turns the saved DIPs into physical pixels with the saved scaling, clamps the rectangle into the working area it overlaps most, and divides by that area's scaling.
- Apply: `MainWindow.RestoreBounds` sets `WindowStartupLocation.Manual`, then `Position` (DIPs × scaling, rounded), then `Width` and `Height`, then maximizes when the saved state was maximized.

The fit clamps the client size, while `Position` is the outer frame's corner, so a window saved against the right or bottom edge can come back overhanging the working area by its frame (YAMV-100).
