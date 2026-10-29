<p align="center">
  <img src="Assets/AppIcon.png" width="128" alt="Slice icon" />
</p>

<h1 align="center">Slice</h1>

<p align="center">
  A lightweight Windows video trimmer for fast cuts, simple volume control, and flexible export.
</p>

<p align="center">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%2F11-0078D4?logo=windows11&logoColor=white" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" />
  <img alt="WinUI 3" src="https://img.shields.io/badge/UI-WinUI%203-2B579A" />
  <img alt="License" src="https://img.shields.io/badge/License-MIT-green" />
</p>

## What is Slice?

Slice is intentionally small. It is not a full nonlinear editor and it does not try to become one.

Open a video, trim the edges, remove unwanted ranges, adjust volume, preview the result, and export.

No account. No cloud. No timeline layers. No effects library. No AI features.

## Features

- Native Windows video preview
- Drag and drop video opening
- Single lightweight timeline
- Direct left and right trim handles
- Mark in / mark out cutting
- Edit-aware preview that skips removed ranges
- Frame stepping
- Master volume control
- Undo support
- MP4, WebM, MKV, GIF, MP3, and WAV export
- Original, 1080p, 720p, and 480p resolution presets
- Original, 60, 30, 24, and 15 FPS presets
- Small, Balanced, and High quality presets
- Lossless stream copy when the edit allows it
- SVG interface icons
- No telemetry or sign-in

## Screens and controls

| Action | Shortcut |
| --- | --- |
| Open video | `Ctrl + O` |
| Play / pause | `Space` |
| Mark in | `I` |
| Mark out | `O` |
| Remove marked range | `Delete` |
| Undo | `Ctrl + Z` |
| Seek | Click or drag the timeline |
| Trim | Drag either timeline edge |

## Tech stack

- C#
- .NET 10
- WinUI 3 / Windows App SDK
- Windows `MediaPlayerElement`
- FFmpeg and FFprobe for media processing

## FFmpeg

Slice looks for `ffmpeg.exe` and `ffprobe.exe` in the following locations:

1. `Tools/` beside the application
2. The application directory
3. Your system `PATH`

The repository does not commit FFmpeg binaries.

## Build from source

Requirements:

- Windows 10 1809 or later, or Windows 11
- .NET 10 SDK

```powershell
./build.ps1
```

Or manually:

```powershell
dotnet restore
dotnet build -c Release -p:Platform=x64
```

## Portable build

```powershell
./publish.ps1
```

Output:

```text
artifacts/portable/Slice-win-x64/
artifacts/portable/Slice-win-x64.zip
```

## Installer build

The installer build uses Inno Setup 6.

```powershell
./build-release.ps1
```

Output:

```text
artifacts/installer/Slice-Setup-x64.exe
```

`build-release.ps1` also creates the portable ZIP.

## Project structure

```text
Slice/
├─ Assets/             App and interface icons
├─ Controls/           Timeline control
├─ Dialogs/            Export UI
├─ Models/             Edit and export models
├─ Services/           FFmpeg probing and export
├─ Tools/              Optional local FFmpeg binaries
├─ MainWindow.xaml     Main UI
└─ Slice.csproj
```

## Philosophy

Slice should stay focused on four things:

**Trim. Cut. Volume. Export.**

Features that turn it into a conventional full video editor are intentionally out of scope.

## License

Slice is released under the [MIT License](LICENSE).

FFmpeg is a separate project and is distributed under its own licenses.
