# LetsClip

## Features
- Keeps the last moments of your game, app or monitor, ready to save with one key
- Games and Apps Only records just that window, so chats and notifications stay out
- One click picks the recommended quality, or record up to your monitor's own resolution and refresh rate
- Records system audio, and your microphone with its background noise cleaned out
- Trim a clip, set its volume and export it as an MP4, under a size limit when you need one
- Keep just the trimmed part without losing any quality, and bring the full clip back anytime
- Name your clips and filter them by the game or app they were recorded in
- Starts with Windows in the tray and updates itself

## Quick start
1. Download the exe from the [latest release](https://github.com/poolfullofmilk/LetsClip/releases/latest)
2. Run it, there is nothing to install
3. Press F10 to save a clip, then open it to trim and export

## ⚠️ Important
- Nothing leaves your PC, and nothing is kept until you save a clip
- Locking Windows, sleep or the display turning off clears the unsaved replay
- HDR is not supported yet, and exclusive fullscreen may not be captured: play borderless
- Windows 10 needs the Microsoft Edge WebView2 Runtime, Windows 11 already has it
- Windows may warn on the first run because the app is not signed: click More info, then Run anyway

## Technical details
- .NET 10, WPF, Blazor and MudBlazor, one self-contained exe
- A bundled FFmpeg 9.0.2 records and exports with your graphics card's encoder (AMD, NVIDIA or Intel), NAudio captures the audio
- GPL-3.0, see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)

## Screenshots

### Clips
![Clips](Screenshots/Clips.png)

### Editor
![Editor](Screenshots/Editor.png)

### Settings
![Settings](Screenshots/Settings.png)
