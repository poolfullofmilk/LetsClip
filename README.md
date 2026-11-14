# LetsClip

## Features
- Keeps the last 30 seconds to 10 minutes of your game, app or monitor, ready to save with one key
- Games and Apps Only records just that window, so chats and notifications stay out, and you pick any installed game or app from one list
- Press the key in a game LetsClip does not know yet and it offers to record that game from then on
- Recommended marks the best quality for your PC, or record up to your monitor's own resolution and refresh rate
- Records system audio, or only your game and voice chat (Discord, Vesktop, TeamSpeak, Mumble, Steam), and your microphone at the volume you set with its background noise cleaned out and quiet words brought up
- Your microphone also gets its own track, so an export can leave out your voice or the game
- Test your microphone live and hear how it will sound in a clip
- A small toast over your full screen game says when a clip is saved
- Clips are named after their game and time, and renaming a clip renames its file, so it shares under that name
- Trim a clip to the exact frame, by dragging, typing or stepping, over a strip that shows its sound, and export an exact copy of it, or fit it to Discord's Free, Nitro Basic or Nitro limit as HEVC at the size that looks sharpest
- Exports keep going when you leave the editor, with their progress in the top bar
- Keep just the trimmed part without losing any quality, and keep the full clip to bring back if you want
- The length, size and bitrate shown before an export are what the file comes out as, and a selection too long for a Discord plan tells you how long it can be
- Clips sit under a header per day, each tagged with its game, app or monitor, with its resolution, bitrate and size in its menu
- Search, rename and sort clips, and filter them by game or to the ones you have not edited yet
- Rest the pointer on a clip to preview it
- Copy any clip as a file and paste it straight into Discord
- Changing the clips folder moves every clip with its details, with a progress bar
- A different sound tells you when a press saved nothing, and no notification ever plays a Windows sound over your game
- A problem, like keys another app already uses or an almost full drive, shows as a small chip next to where you fix it
- Spamming the key saves one clip, not a pile of copies
- Finds your games wherever Steam is installed, games from the Xbox app and the ones Windows saw you play from Battle.net, Ubisoft or EA, even ones installed while it runs
- A game always goes before an app you added, so a browser left open never takes its place
- Change the replay length while it records, nothing is lost
- Remembers where you left its window and how loud you play clips
- Starts with Windows in the tray, updates itself in the background with signed updates, shows what changed and recovers from problems on its own, even a crash
- Uninstalls from Windows' own Apps list, and your clips stay

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
- Its own slim FFmpeg 9.0.2 build records and exports with your graphics card's encoder (AMD, NVIDIA or Intel) and converts the picture on the card where it can, NAudio captures the audio
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
