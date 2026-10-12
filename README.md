# BrightnessTray

A minimal Windows tray app that adjusts display brightness, styled after the
Windows volume flyout. Built with .NET 10 and WinUI 3 (Windows App SDK),
unpackaged and fully self-contained.

## Features

- **Left click** the tray icon: flyout with a single focused slider (arrow keys
  work immediately). Applies to every controllable display at once.
- **Scroll wheel** over the tray icon: brightness in ±5% steps (works even
  while the flyout is closed).
- **Right click**: menu with `Autostart with Windows`, `Restore brightness on
  startup` and `Exit`.
- External monitors are controlled through DDC/CI, built-in laptop panels
  through WMI. Displays without brightness support are listed as
  `— no DDC/CI`.

## Build

Requires the .NET 10 SDK.

```
dotnet build -c Release
```

## Publish (single self-contained exe)

```
dotnet publish -c Release
```

Output: `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\BrightnessTray.exe`
(~95 MB; .NET 10 runtime, Windows App SDK and the tray icon are embedded).
On first launch the native libraries self-extract to `%TEMP%\.net\BrightnessTray`,
so the exe can start slightly slower the very first time.

## Notes

- Single instance: a second launch exits immediately.
- Autostart is the `HKCU\...\Run` value `BrightnessTray`, synchronized with the
  setting on every start (deleted value self-heals on next launch).
- Settings live in `%APPDATA%\BrightnessTray\settings.json` and are never
  reset on launch.
- The legacy WinForms implementation is kept in `BrightnessTray.cs` as a logic
  reference and is excluded from the build.
