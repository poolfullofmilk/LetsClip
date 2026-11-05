# LetsClip

## Features
- Keeps the last 30, 60 or 120 seconds of one monitor in the background, ready to save
- Press a hotkey anywhere, even in a game, and the last moments become a clip
- Records from 480p up to your monitor's own resolution, up to its refresh rate, 240 FPS included, at Normal, High or Max quality, with the heavier picks marked in amber and red
- Records what your speakers or headphones play, with your microphone mixed in if you want it, each following the Windows default device or one you pick
- Games Only records just the game's own window at its own shape while it runs, so chats, other apps and notifications stay out of your clips, from a list of found games you can add to, from your open apps or any exe, or remove from
- Starts with Windows in the tray and keeps recording there until you exit it
- Trim a clip by dragging the handles, stepping them with the arrow keys or setting them at the frame you watch, and the trim is kept for next time
- Saved clips are exactly the replay length you picked
- Export an MP4 at its best quality or under a size limit (20 MB to 500 MB, only the ones that matter for the clip), at 60 FPS or the clip's own rate
- Copy the export and paste it anywhere, drag it into another app, or open its folder
- Watch a clip full screen, and delete clips you do not want, they go to the Recycle Bin
- Plays a soft chime the moment you save a clip, never heard in the clip itself
- Updates itself

## Quick start
1. Download the exe from the [latest release](https://github.com/poolfullofmilk/LetsClip/releases/latest)
2. Run it, there is nothing to install, and it starts recording right away
3. When something happens, press Ctrl+Shift+F8, then open the clip, trim it and export it

## ⚠️ Important
- LetsClip records everything visible on the chosen monitor, notifications and chats included, into a short rolling cache on your PC, or with Games Only just the game's window. Nothing is kept until you save a clip and nothing leaves your PC
- Locking Windows, sleep, the display turning off or a monitor change clears what was not saved, then recording starts over by itself
- HDR monitors are not supported yet: turn HDR off for the monitor you record
- Games in exclusive fullscreen may not be captured, borderless or windowed works best
- Windows 10 needs the Microsoft Edge WebView2 Runtime, Windows 11 already has it
- Windows may warn on the first run because the app is not signed: click More info, then Run anyway

## Technical details
- .NET 10, WPF, Blazor and MudBlazor, one self-contained exe
- Capture, encoding and export run on a bundled FFmpeg 9.0.2 (GPL v3 build) with your graphics card's encoder when it has one (AMD, NVIDIA or Intel), audio on NAudio through WASAPI
- Recording a 1440p monitor as 1080p at 60 FPS takes about half of one CPU core, at 1440p and 240 FPS about two; with Intel graphics the colour conversion runs on the graphics card too, about a fifth of a core at 1080p60
- GPL-3.0, see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for FFmpeg, NAudio and the rest

## Screenshots

### Clips
![Clips](Screenshots/Clips.png)

### Editor
![Editor](Screenshots/Editor.png)

### Settings
![Settings](Screenshots/Settings.png)
