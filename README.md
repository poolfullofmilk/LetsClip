# LetsClip

![Demo](Demo.webp)

## Features
- Keeps the last moments of your game, app or monitor in the background, saved with one key
- Finds your games by itself and records just their window, with the game and voice chat or everything your PC plays
- Your microphone cleaned of background noise, on a track of its own
- A crash or a closed game still leaves the moments before it to save
- Clips grouped by day and game, with search, filters and a preview on hover
- Trim to the frame and crop freely, without losing quality
- Exports that fit Discord's limits, or copy a clip straight into Discord
- Deleted clips and cut originals come back from the Recycle Bin in one click
- Updates itself in the background

## Quick start
1. Download the exe from the [latest release](https://github.com/poolfullofmilk/LetsClip/releases/latest)
2. Run it, it puts itself in place
3. Press F10 to save a clip, then open it to trim and export

## ⚠️ Important
- Nothing leaves your PC
- Locking Windows, sleep or the display turning off clears the unsaved replay
- HDR is not supported, and borderless works better than exclusive fullscreen
- Made for Windows 11; Windows may warn on the first run since the app is not signed: More info, then Run anyway

## Technical details
- .NET 11, WPF, Blazor and MudBlazor in one self-contained exe
- Its own slim FFmpeg build records and exports on your graphics card (AMD, NVIDIA or Intel)
- GPL-3.0, see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)
