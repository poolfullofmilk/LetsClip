# LetsClip

An instant replay recorder for one monitor that runs in the background. A WPF window hosts a `BlazorWebView` whose UI is MudBlazor, laid out like `SoTF.Editor`, with `WiFiWatch`'s tray and startup registration. It starts with Windows in the tray and keeps the last 30, 60 or 120 seconds of the chosen monitor (the primary until one is picked) and system audio (optional microphone) in a rolling cache, all the time or only while a game runs (`AFK-Assist`'s `GameScanner`). A hotkey saves them as an MP4 clip, and the clip editor trims it and exports an MP4 under a size limit for Discord.

| Project | Holds |
|---|---|
| `LetsClip.Data` (`net10.0`) | `Helpers` (`AppInfo`, `Formatter`, `ClipNames`, `SizeBudget`, `TrimRange`, `Hotkey`, each with a Debug self test), `ViewModels` (records the UI and services share), `Enums` (`CaptureHolds` among them) |
| `LetsClip.Services` (`net10.0-windows`) | `Capture` (`ReplayRecorder` state and saves, `CaptureSession` the two FFmpeg processes, `AudioMixer` WASAPI onto the media clock, `SegmentCache`, `ClipSaver`, `MonitorCatalog` DXGI, display config and refresh rates, `AudioDevices`, `CacheFolders`, `CaptureController` the app's one entry point, its automatic buffering and the games list), `Games` (`GameScanner` copied from `AFK-Assist`, `GameWatcher`), `Integration` (`StartupRegistration` copied from `WiFiWatch`, `Updater`), `Media` (`FFmpeg` process runner with a kill-on-close job, `MediaProbe`, `VideoEncoders`, `ClipLibrary`, `ClipExporter`), `Storage` (`UserSettings`) |
| `LetsClip.Desktop` | `App` (startup, `--tray`, logging, single instance, lock, sleep and display events), `MainWindow` (WebView2 host, tray, close to tray, virtual hosts), `Interop` (`DesktopShell` for the page, `MonitorOverlay`, `WindowMessages` for the hotkey and display power, `TrayMenu`, `TrayBalloon`, `SaveChime`, `Explorer`, `WindowCaptionTheme`), `Components`, `Theming`, `wwwroot`, `FFmpeg` |

Packages: MudBlazor, `Microsoft.AspNetCore.Components.WebView.Wpf`, NAudio.Wasapi 3, NLog. FFmpeg 9.0.2 (BtbN GPL shared build, pinned by SHA-256 in `LetsClip.Desktop/FFmpeg`) does capture, encoding, segmenting, remuxing, probing, thumbnails and export as child processes. No database.

## Build, Run And Publish

The FFmpeg binaries are not in git. After cloning, once:

```bash
powershell -ExecutionPolicy Bypass -File LetsClip.Desktop/FFmpeg/Get-FFmpeg.ps1
```

```bash
dotnet build LetsClip.slnx
```

```bash
dotnet run --project LetsClip.Desktop
```

```bash
dotnet publish LetsClip.Desktop -c Release
```

The publish folder holds only `LetsClip_v<Version>.exe`: `IncludeAllContentForSelfExtract` and the `BundleWebRoot` target pack `wwwroot` and the `FFmpeg` folder inside it, extracted once on first start. The exe is ReadyToRun (`PublishReadyToRun`), so the first page opens sooner. A build without the FFmpeg files stops with a message (`RequireFFmpeg`).

`.github/workflows/build.yml` fetches the pinned FFmpeg and builds in Release on `windows-latest` for every push and pull request. Releases are made by hand, the shared way: the exe as the only asset and an empty body, so the update dialog shows no notes.

A Release exe someone started from `bin\Release` locks that folder; publish to another folder with `-p:BaseOutputPath=<folder>\` rather than stopping it.

### Screenshots

Fresh ones for every release into `Screenshots/`: `Clips.png` (the capture panel buffering beside clip cards), `Editor.png` (a trimmed clip) and `Settings.png`. Neutral demo clips only, made with FFmpeg's `lavfi` sources: a real clip shows whatever was on the owner's screen. Run the published exe with `LETSCLIP_ROOT=C:\LetsClip`, so every shown path is generic, and check no text names the owner before each shot. Over CDP, `Emulation.setDeviceMetricsOverride` at `1600 x 845` and scale `1.6` gives `2560 x 1352`. The README stays short, the owner's pick: no tagline or picture under the title, one plain bullet per feature, and every shot in the last section, Screenshots, one column under small titles.

## Where Data Lives

| What | Where |
|---|---|
| Settings (`UserSettings`) | `%AppData%\LetsClip\settings.json`, written through a temporary file and a rename |
| Clip metadata (trim, logical range, recorded time) | `%AppData%\LetsClip\Clips\<hash of path>.json`, ignored when the file's size or write time no longer match |
| WebView2 profile | `%AppData%\LetsClip\WebView2` |
| Replay cache | `%LocalAppData%\LetsClip\Cache\<session guid>\seg_*.ts` and `aud_*.ts` |
| Thumbnails and timeline strips | `%LocalAppData%\LetsClip\Thumbnails` |
| Logs | `Documents\LetsClip\Logs` |
| Saved clips | `Videos\LetsClip` (the Videos known folder) or the folder picked in Settings |
| Exports | `<clips folder>\Exports` |
| Start with Windows | `HKCU\...\Run` value `LetsClip` with `--tray`, and `Start Menu\Programs\01 Apps\LetsClip.lnk`, both rewritten on every Release launch |

A Debug build uses `LetsClip Debug` for all of these (`AppInfo.FolderName`) and never registers with Windows. `LETSCLIP_ROOT` moves every one of them under a scratch folder, gives the app its own single instance handle and skips the registration, for tests.

## Automatic Buffering

- Buffering has no switch: `CaptureController` starts it at launch, after an unlock, a wake, the display coming back and a monitor returning, once the first run notice was answered Yes. The panel shows Start Buffering only before that answer, and Try Again after an error.
- `CaptureHolds` (locked, asleep, display off) stop it and clear the history; it starts again only when no hold is left. Displays that sleep after the Windows timeout count, so an app left alone stops buffering on its own.
- The tray's Pause Buffering pauses every automatic start until Resume Buffering or a restart (`CaptureController.IsPaused`).
- Games Only records the game's own window, never the desktop: alt tab, chats and notifications stay out of the clip. `GameWatcher` scans the running processes every two seconds for a known game with a window on screen (not minimised), so a game started before LetsClip is found too, and counts it until its process exits. Known games are `GameScanner`'s Steam, Epic, Riot, Rockstar and Roblox libraries plus `Games` folders on C, D and E, with `UserSettings.AddedGames` added and `RemovedGames` taken out (`CaptureController.GameList`). Turning Games Only on or off stops the capture and starts the other source.
- Settings → Games Only → the blue N Games link opens `GamesDialog`: Remove keeps the key in `RemovedGames`, so the game stays greyed at the bottom with Add Back; Add Game takes an exe, keyed by `GameScanner.NormalizeProcessKey` of its file name and named by its product name.
- Changing the monitor, replay length, resolution, frame rate or audio while buffering restarts the capture at once with a fresh history.
- Starts and stops go through one semaphore, whoever asks: the page, the tray, Windows events or the game timer.
- A session that ran 30 s or more and then dropped (the encoder exited, a game recreated its window) starts over by itself 3 s later (`ReplayRecorder.Dropped`); a capture that fails to start stays on its error. The reason names the exit code.

## Capture

- Monitors: `ddagrab` (Desktop Duplication) on the adapter and output that `MonitorCatalog` finds for the saved monitor's device path, never a stored index. The frame comes back unrotated: rotation 90 is `transpose=clock`, 270 `transpose=cclock`, 180 both flips (checked on the portrait LS27AG500N at 270).
- Game windows: `gfxcapture` (Windows Graphics Capture) on the window handle `GameWatcher.GameWindow` reads fresh at each start, with no yellow border, sized on the GPU to the output box of the monitor holding the window (`MonitorCatalog.MonitorOf`, `resize_mode=scale_aspect`, so a windowed game gets bars). It sends a frame only when the window changes, so keyframes are forced each second of time (`-force_key_frames`), and the video stall check and slow frame rate warning are off for it: a minimised game sends nothing and only its exit ends the session.
- Resolution is 1080p (fit 1920 x 1080 in the monitor's orientation, never up) or the monitor's own size (`FullResolution`), offered only when its short side is above 1080 and labelled by that side (1440p). Frame rates are `UserSettings.FrameRateChoices` up to the monitor's refresh rate, which `MonitorCatalog.RefreshRateOf` reads with `EnumDisplaySettingsW` into a raw `DEVMODE` (`dmDisplayFrequency` at byte 184).
- `hwdownload`, then one CPU `scale` with BT.709 limited range, then `nv12`. Measured with the window shown: 2560 x 1440 to 1080p at 60 FPS takes about half of one CPU core for the video process, 2560 x 1440 native at 240 FPS about two cores and 606 MB, holding 240 FPS (7198 frames in a 30 s clip).
- No GPU-only route works on the RX 9070 XT with FFmpeg 9.0.2, all retried in November 2026: D3D11 frames straight into `h264_amf` fail in `SubmitInput` (error 18, DirectX failed), `scale_d3d11` cannot create its NV12 texture (`E_INVALIDARG`), `vpp_amf` fails `AMFConverter-Init` (error 10) whatever its colour options, `hwmap` to AMF is not implemented, Vulkan mapping is not implemented for D3D11, and `vsrc_amf` rejects 240 FPS while its frames fail encoder init at lower rates. Downloaded BGRA fed to AMF halves the CPU at 240 FPS (0.84 cores against 1.70), but AMF's own conversion scored 36.6 dB on colour bars against 44.6 dB for the `scale` above, and its range follows the input's tags (full range from `ddagrab`), so it is not used. `fast_bilinear` saves no CPU per frame and drops to 39.6 dB. Colour checks use known bars, never the live screen, whose frames already differ at 24 dB.
- The encoder is the first that really encodes a few test frames (`VideoEncoders.WorksAsync`): AMF, NVENC or Quick Sync for each GPU vendor present, then x264. A one second GOP, no B frames, and `h264_metadata` writes the BT.709 colour description the hardware encoders leave out. Rates grow with the pixel rate: load is the square root of pixels per second over 1080p60 (at least 1), the average 20 Mbps and the peak 40 Mbps times the load. Only AMF and x264 are tested on this PC.
- One second MPEG-TS segments from the `segment` muxer, each listed on standard output as `name,start,end`, with `mpegts_copyts=1` so the files keep the media clock. `SegmentCache` keeps the replay length plus three seconds, within 1.5 GB or the replay length at the peak rate plus 20%, whichever is larger, and never evicts past a pinned save.
- Audio runs in a second FFmpeg process. In one process the scheduler held the live video back whenever the piped audio lagged the video by 100 ms, and capture fell to 6 FPS; apart, both run free.
- `FFmpeg.Start` opts every child out of power throttling (`SetProcessInformation`, execution speed and timer resolution). Windows throttles the children of an app whose window is shown, even when covered, and capture fell to 35 to 45 FPS whenever the LetsClip window was open; hidden to the tray it was always 60. A sibling FFmpeg started outside LetsClip never slowed, which is how it was found.
- The media clock: `AudioMixer` places each WASAPI packet at its QPC time (sample zero is the session's start, media time 1.0), silence and gaps stay silence, and a packet within 20 ms of the last one continues it. Video gets onto the same clock with one wall clock reading at its first frame (`setpts` with `RTCTIME`). Both clocks are read in microseconds.
- A save pins the cache, waits for the segment holding the key press to close, joins the segments with the `concat:` protocol and stream copies them up to the press into an MP4 named `LetsClip yyyy-MM-dd HH-mm-ss.mp4` (`ClipNames`). Keyframe pre-roll stays in the file; the metadata's `KeepStart` and `KeepEnd` hold the asked range, which the editor opens on.
- A save plays `SaveChime` (two soft bell notes a fifth apart, a third of a second) instead of the Windows sound, when Save Sound is on, and `AudioMixer.MuteSystemAudio` writes the loopback as silence for that long, so the chime never lands in the next clip. The tray balloon is silent: `TrayBalloon` sends `NIIF_NOSOUND` through `Shell_NotifyIconW`, borrowing `NotifyIcon`'s private `_window` and `_id`.
- Lock, sleep, display off, a monitor change and a stalled encoder all end the session and clear its history; nothing splices across them.

## Export

- Re-encodes the trimmed range from the saved clip: `-ss` before `-i` (frame accurate when transcoding), `scale` plus `pad` to even sizes, `yuv420p`, BT.709 tags, AAC 128 kbps, `faststart`.
- Size Limit is a slider from 1 MB to the size that reads Sharp twice over at the chosen resolution, frame rate and audio (`SizeBudget.CeilingMegabytes`, 0.09 bits per pixel, at least 2 MB), 10 MB by default for Discord's free limit, which its info icon explains. A static clip once capped the slider at 2 MB when the cap followed the source size. `ClipExporter` clamps the request the same way, since 4000 MB failed in FFmpeg with Result Too Large. The picked size is saved when an export starts, not on every slider step.
- Frame Rate is 60 FPS or the clip's own when it runs above 60 (`ExportFullFrameRate`, `ClipExporter.FrameRateOf`); a 240 FPS clip at 10 MB is otherwise Too Small.
- `SizeBudget` turns the limit (decimal MB) into a video bitrate after a 5% margin, the MP4 index and audio. Bits per pixel decide Sharp, Good, Soft or Too Small; Too Small refuses.
- The hardware encoder goes first (single pass VBR aimed at 90% of the budget, since AMF overshoots), x264 two pass second. Every result is probed and its real size checked; over the limit retries at a lower bitrate, twice per encoder. On a 30 second noise heavy 1080p60 test AMF landed 18.4 MB for 20 MB and 9.3 MB for 10 MB on the first try in about 9 s, and scored VMAF 85 against x264 two pass 74 at the same size (28 s). A 12 s 1080p120 test pattern landed 8.7 MB for 10 MB at 60 FPS.

## Updates

`Updater.CheckAsync` reads the `releases/latest` redirect without following it, so it needs no token. It runs on every start (no setting) and from **Help → Check for Updates**; a newer version opens `UpdateDialog` and a red dot stays until restart: a `MudBadge` on Help (`d-flex`, or it lifted Help 2 px), then `update-dot` on Check for Updates. What Changed shows the release body's lines (`AppInfo.ReleaseApiUrl`); an empty body hides the list.

Update Now hides the X and Escape until the download fails (`UpdateDialog.SetClosableAsync`). `Updater.DownloadAsync` saves the new exe beside the running one through a `.download` file that must match the asset's SHA-256 `digest`; a mismatch or missing digest opens the release page instead. `Updater.Launch` starts it with `--after-update <pid>` and the window exits through `MainWindow.ExitAsync`, finishing saves. The new exe runs `Updater.FinishPreviousVersion` before the single-instance check: it waits up to 15 s for the old process and deletes every other `LetsClip_v*.exe` beside it. A protected folder, a Debug build or a renamed exe (`Updater.CanInstall`) opens the release page. Build with `-p:Version=0.9` to see the dialog.

## Text

- Labels are short Title Case, and inside one the short joining words stay lower case, the owner's pick: a, an, the, and, or, as, at, by, for, in, of, on, per, to, with (Start with Windows, Show in Folder). The first and last word keep their capital. Comments keep every word capitalised, RazorStyle checks that.
- Longer sentences (descriptions, hints, warnings, snackbars) are sentence case, short, and never end in a symbol; with two sentences only the last goes without a full stop.
- A field or section that needs explaining gets a tonal blue `InfoIcon` in a tooltip, never a caption.
- A value about a choice sits greyed (`mud-text-secondary`) on the right where the row has room, never in brackets: a monitor's size and refresh rate in its select item, Default beside the Windows default audio device (`AudioDeviceInfo.IsDefault`), the clips folder, the export size. Device names drop their endpoint (Headphones), unless two devices would then share a name.
- Settings and panel rows are a title and their control, no description under them (the owner's pick); the capture and export panels use the same `SettingRow`. The status chip in the app bar is a plain chip, no tooltip or click, and names the game it records (Buffering Overwatch).
- No full path on screen: the clips folder shows relative to the profile (`Videos\LetsClip`), and the empty Clips page names the hotkey, not the folder.
- Numbers follow the Windows format (`Formatter` uses the current culture). Every shown date goes through `Formatter.FormatDateTime`, `dd/MM/yyyy HH'h'mm` (`04/11/2026 22h43`); file names keep the sortable `yyyy-MM-dd HH-mm-ss`. A clip still under its generated name shows its date as its title; a renamed file shows its name.

## Layout

- The editor fills the window and ends `--page-bottom` above it (`page-fill`, `fill`, `fill-column`); a dialog table scrolls under its fixed header (`.mud-table.fit` with a `max-height`).
- Clip cards sit straight on the page in a `MudGrid Spacing="3"`, never inside a panel. Card actions always show, never only on hover.
- Inputs never stretch with the window: a select sits in a `MudElement` with `fixed-width` (`wide` for device names), because `MudSelect` puts `Class` and `Style` on an inner element.
- Buttons are never outlined: filled for actions, text for quiet ones. A choice of two or three is a `MudButtonGroup` (`OverrideStyles` false) of small filled buttons, `Color.Info` when picked; more choices are a select (the six frame rates overflowed the capture panel). No ripple.
- A button that cannot do anything is disabled, and a disabled control gets no tooltip: `Tooltip` takes `Disabled` with the same condition.
- A dialog closes with its X only, and its action says what it does (Add Game, Update Now). `ConfirmDialog` keeps Yes and No.
- Sliders get a real 16 px thumb, a rounded filled track and a focus ring in CSS: MudBlazor scales a 2 px circle, lopsided at 125%.
- The player shows the clip at its own shape, centred, with the inner radius and the panel shadow, never a frame or black bars beside it. Its controls are one row of icon buttons in tooltips (time left, transport centred, mute and full screen right), the trim values sit under the timeline, and there is no hint text. Full screen hands the video to the browser's own controls, `MainWindow` takes the whole monitor while `ContainsFullScreenElement` holds, and Escape or a double click leaves.
- Clips delete to the Recycle Bin after a question (`Explorer.Recycle`), from the card's bin button or the editor's header; the editor lets go of the file first. The Clips header keeps only Open Clips Folder, since the library watches its folder.

## Looks Wrong, Is Not

- Each capture writes two segment streams (`seg_` video, `aud_` audio); the save joins them by their shared timestamps.
- `ClipSaver` writes the clip as `.LetsClip-<guid>.mp4` and renames it only after it probes back; such leftovers after a crash are ignored by the library and never deleted, since the clips folder belongs to the user.
- `CacheFolders.Delete` refuses anything but a GUID folder directly under the cache root and only deletes files named like segments or x264 pass logs.
- The preview is muted while buffering: unmuted, its sound would land in the next clip.
- Leaving the editor cancels a running export.
- A warm-up clip's file starts with about a second of audio before the first video frame, and that first frame can show content older than its timestamp (`ddagrab` probes it at start). Both sit before `KeepStart`, so the editor and exports never show them.
- The video FFmpeg holds about 600 MB of private memory, flat over a 10 minute run; the audio one about 23 MB.
- Opening the monitor list shows a `MonitorOverlay` on every monitor (accent edge and name on the picked one, grey on the others), and a pick stays outlined 1.5 s. The overlays are click through, never activate and carry `WDA_EXCLUDEFROMCAPTURE`, so a screen grab or a clip never shows them even while buffering.
- The trim handles sit outside the kept part, their inner edge on the time, so two handles never cross or overlap; the timeline pads both ends by a handle's width (`--handle-width`) and maps time over the inner `timeline-track`. Each handle is rounded only on its outer side, and a drag keeps the point where it was grabbed.
- Play runs from the playhead and stops at the end point, starting over from the start point once there; the 5 second jumps clear that stop when they pass it.
- The timeline strip decodes keyframes only, one a second, so it samples 12 frames over the duration less one second; spread over the whole clip, the last tile of a short one stayed black.
- A clip with no metadata reads its recording time from its generated name (`ClipNames.DateOf`), so a copied file keeps its date.
- WebView2's browser keys, zoom and pinch zoom are off (`AreBrowserAcceleratorKeysEnabled`, `IsZoomControlEnabled`, `IsPinchZoomEnabled`).

## Verify

- `dotnet build LetsClip.slnx`, then the analyzer, CSharpier, RazorStyle, line ending and comment checks from the shared rules.
- Debug self tests run on first use of each helper; a failed one fails fast.
- Real media checks live as throwaway file-based apps: a pattern window that shows a millisecond barcode and a flash plus a 1 kHz beep each second, a capture harness on the Services, and an analyser that reads the barcode and beeps back out of a clip. Last results: every frame decoded, clip end within 20 ms of the key press, audio about 30 ms behind video (plus or minus 5) and steady across 22 s of silence, a trimmed export within 15 ms of its in and out points.
- Count frames per second of a saved clip with the window shown and hidden; both must hold the picked rate.
- The automation is checked from a harness that drives `CaptureController` directly (a game appearing and closing, holds, a hand pause surviving an unlock, the notice starting capture), so nothing takes the foreground. `StartupRegistration` is checked in Release without and with `LETSCLIP_ROOT`, removing the Run value and shortcut afterwards.
- Look at the app over CDP: start it with `LETSCLIP_ROOT` set to a scratch folder and `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333`; `http://127.0.0.1:9333/json` lists the page. `Emulation.setDeviceMetricsOverride` lasts only while its WebSocket stays open, so size, act and shoot in one session. A `MudSelect` opens only on real `Input.dispatchMouseEvent` presses; its items and buttons take a script `click()`. A dialog's scrim takes every click. Sweep at `1600 x 845` and `1280 x 680` for anything past its panel or a sideways page scroll.
- With the displays asleep the display hold keeps a sandboxed app from buffering; check capture while someone is at the PC.