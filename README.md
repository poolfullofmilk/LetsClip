# LetsClip

## Features
- Keeps the last 30, 60 or 120 seconds of your game or monitor, ready to save
- A hotkey saves them as a clip, even in a game
- Games Only records just the game's window, so chats and notifications stay out
- Records up to your monitor's resolution and refresh rate, at Normal, High or Max quality
- Records system audio, plus your microphone with its background noise cleaned out
- Trim a clip and export it as an MP4, under a size limit when you need one
- Starts with Windows in the tray and updates itself

## Quick start
1. Download the exe from the [latest release](https://github.com/poolfullofmilk/LetsClip/releases/latest)
2. Run it, there is nothing to install
3. Press Ctrl+Shift+F8 to save a clip, then trim and export it

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
