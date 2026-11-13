# LetsClip

## Features
- Keeps the last 30 seconds to 10 minutes of your game, app or monitor, ready to save with one key
- Games and Apps Only records just that window, so chats and notifications stay out, and you pick any installed game or app from one list
- Press the key in a game LetsClip does not know yet and it offers to record that game from then on
- One click picks the recommended quality, or record up to your monitor's own resolution and refresh rate
- Records system audio, or only your game and Discord, and your microphone at the volume you set with its background noise cleaned out and quiet words brought up
- Test your microphone live and hear how it will sound in a clip
- Trim a clip to the exact frame, by dragging, typing or stepping, and export an exact copy of it, or fit it to Discord's Free, Nitro Basic or Nitro limit as HEVC at the size that looks sharpest, with or without sound
- Exports keep going when you leave the editor, with their progress in the top bar
- Keep just the trimmed part without losing any quality, and keep the full clip to bring back if you want
- The length, size and bitrate shown before an export are what the file comes out as, and a selection too long for a Discord plan tells you how long it can be
- Clips sit under a header per day, each tagged with its game, app or monitor, with its resolution, bitrate and size one hover away
- Rename clips, sort them and filter them by game or to the ones you have not edited yet
- Rest the pointer on a clip to preview it
- Copy any clip as a file and paste it straight into Discord
- A different sound tells you when a press saved nothing, and no notification ever plays a Windows sound over your game
- A problem, like keys another app already uses, shows as a small chip next to where you fix it
- Spamming the key saves one clip, not a pile of copies
- Finds your games wherever Steam is installed, and games from the Xbox app, even ones installed while it runs
- A game always goes before an app you added, so a browser left open never takes its place
- Change the replay length while it records, nothing is lost
- Remembers where you left its window and how loud you play clips
- Starts with Windows in the tray, updates itself in the background with signed updates and recovers from problems on its own, even a crash

## Quick start
1. Download the exe from the [latest release](https://github.com/poolfullofmilk/LetsClip/releases/latest)
2. Run it from anywhere, it puts itself in place and there is nothing to install
3. Press F10 to save a clip, then open it to trim and export

## ⚠️ Important
- Nothing leaves your PC, and nothing is kept until you save a clip
- Locking Windows, sleep or the display turning off clears the unsaved replay
- HDR is not supported yet, and exclusive fullscreen may not be captured: play borderless
- Windows 10 needs the Microsoft Edge WebView2 Runtime, Windows 11 already has it
- Windows may warn on the first run because the app is not signed: click More info, then Run anyway

## Technical details
- .NET 11, WPF, Blazor and MudBlazor, one self-contained exe
- A bundled FFmpeg 9.0.2 records and exports with your graphics card's encoder (AMD, NVIDIA or Intel), NAudio captures the audio
- Discord exports are HEVC, which Discord plays on desktop and web and almost every phone decodes; the card's H.264 takes over where HEVC is missing
- GPL-3.0, see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)

## Screenshots

### Demo
![Demo](Screenshots/Demo.webp)

### Clips
![Clips](Screenshots/Clips.png)

### Editor
![Editor](Screenshots/Editor.png)

### Settings
![Settings](Screenshots/Settings.png)
