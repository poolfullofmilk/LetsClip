# LetsClip

An instant replay recorder for one monitor or one game or app window that runs in the background, a lightweight Medal.tv in the owner's words. A WPF window hosts a `BlazorWebView` whose UI is MudBlazor, laid out like `SoTF.Editor`, with `WiFiWatch`'s tray and startup registration. It starts with Windows in the tray and keeps the last 30, 60 or 120 seconds of the chosen monitor (the primary until one is picked) or of a known game's or added app's window (`AFK-Assist`'s `GameScanner`), with system audio (or, with Only Record Game and Discord, just the game's and Discord's sound) and an optional microphone, in a rolling cache. A hotkey saves them as an MP4 clip of exactly that length. The Clips page lists them under their game's name with their resolution and frame rate and filters them by game; the editor names a clip, trims it to the frame, cuts it losslessly (Export and Save, with Restore Original) and exports an exact copy of the selection, or fits it under a Discord plan's limit. Any clip copies to the clipboard as a file from its card or the editor. It checks for updates by itself and installs them in the background once it has sat hidden for a while.

| Project | Holds |
|---|---|
| `LetsClip.Data` (`net11.0`) | `Helpers` (`AppInfo`, `Formatter`, `ClipNames`, `SizeBudget`, `TrimRange`, `Hotkey`, `FilePaths`, each with a Debug self test), `ViewModels` (records the UI and services share), `Enums` (`CaptureHolds` among them) |
| `LetsClip.Services` (`net11.0-windows`) | `Capture` (`ReplayRecorder` state and saves, `CaptureSession` the two FFmpeg processes, `AudioMixer` WASAPI from devices or from the game's and Discord's process trees onto the media clock, `SegmentCache`, `ClipSaver`, `MonitorCatalog` DXGI, display config and refresh rates, `AudioDevices`, `MicrophoneMeter` the Settings microphone test, `CacheFolders`, `CaptureController` the app's one entry point, its automatic buffering and the games list), `Games` (`GameScanner` copied from `AFK-Assist`, `GameWatcher`), `Integration` (`StartupRegistration` copied from `WiFiWatch`, `Updater`), `Media` (`FFmpeg` process runner with a kill-on-close job, `MediaProbe`, `VideoEncoders`, `ClipLibrary`, `ClipExporter`), `Storage` (`UserSettings`) |
| `LetsClip.Desktop` (`net11.0-windows`) | `App` (startup, `--tray`, logging, single instance, lock, sleep and display events), `MainWindow` (the page host alone: WebView2, its suspend, release and rebuild, full screen, virtual hosts, close to tray), `Tray` (the tray icon, its menu and balloons, the save from the hotkey, exit, update checks and background installs; it owns `WindowMessages`), `Interop` (`DesktopShell` for the page, `MonitorOverlay`, `WindowMessages` a plain hidden window of its own for the hotkey and display power, `TrayMenu`, `TrayBalloon`, `SaveChime`, `PageScript` (every call into the page script, a closing window swallowed), `Explorer`, `WindowCaptionTheme`), `Components`, `Theming`, `wwwroot`, `FFmpeg` |

Packages: MudBlazor 9.12.0, `Microsoft.AspNetCore.Components.WebView.Wpf` 11.0.0, NAudio.Wasapi 3.3.0, NLog 6.2.2 (all the newest on 11 November 2026, none vulnerable, transitive ones included), on .NET 11 since v2.0.4. FFmpeg 9.0.2 (BtbN GPL shared build, pinned by SHA-256 in `LetsClip.Desktop/FFmpeg`) does capture, encoding, segmenting, remuxing, probing, thumbnails and export as child processes. No database.

## Read When A Round Touches It

Everything below is part of this file and binds the same way. Read the whole file for a subject before changing anything in it, and update it in step 7.

| File | Holds | Read When |
|---|---|---|
| `Docs/Capture.md` | Automatic recording, capture, timing, audio, saves | Touching `Capture`, `Games` or anything a clip's content depends on |
| `Docs/Export.md` | Fit for Discord, lossless cuts, trim points, exports | Touching exports, cuts or the editor's trim |
| `Docs/Layout.md` | Text and layout rules, colours, radii, shadows, every owner pick about looks | Touching any page, component, CSS or page script |
| `Docs/Recovery.md` | The Recovery table, reliability research, injected failures | Touching retries, encoders, crashes or anything that can fail |
| `Docs/Release.md` | Publish notes, screenshots and the demo, updates and installs | Releasing, or touching `Updater`, `StartupRegistration` or the csproj |
| `Docs/FFmpeg.md` | What to run again on every FFmpeg update | Changing the pinned FFmpeg |
| `Docs/Verify.md` | How to verify, the tools, baseline costs | Step 6 of every round |
| `Docs/Measurements.md` | Dated audits, sweeps and field tests | Needing a number to compare with, or adding a measured round |

## Every Round, Start To Release

Do every step below, in order, every round, without being reminded: the owner should never have to repeat any of it. Be thorough and be sure. Measure rather than eyeball, look at every screen the round touched, and never call something done that was not seen working.

1. `git pull` first, since the owner edits the README on GitHub.
2. Read every file the change touches and its callers before editing. A problem the owner points out once is a pattern: search the whole app for the same control, wording, spacing or shadow and fix every place in the same round.
3. Build with `dotnet build LetsClip.slnx`: zero errors and zero warnings. A running sandbox locks `bin`, so stop it first.
4. Run every check: `dotnet format analyzers` and `dotnet format style` with `--verify-no-changes --severity info` on each of the three projects, `csharpier format .` then `csharpier check .`, `powershell -File ~/.claude/scripts/RazorStyle.ps1`, `git ls-files --eol` showing no `w/lf` and no `w/-text`, and the comment length check over the C#, `LetsClip.css` and `AppShell.js`. Normalising line endings with `sed` gives a file without a final newline a lone CR at its end, which git then reads as binary (`w/-text`): trim that byte.
5. Run a ponytail ultra audit over what the round touched and anything it grew: dead parameters and fields, single use wrappers, markup repeated where one shared component belongs (like `ChoiceSelect`), settings nobody needs. Apply what it finds and say what was cut.
6. Look at it in a sandbox: a Debug build with `LETSCLIP_ROOT` on a scratch folder, `TestClips` copied into its clips folder, a 30 MB noisy clip and a 110 MB 1440p one made with `testsrc2` and `noise` so every Discord plan has work, and settings whose `RemovedGames` holds any game that is running, so nothing records the owner's game.
   - Sweep over CDP at 1920 x 1009, 1600 x 845, 1440 x 760, 1366 x 705, 1280 x 680 and 1264 x 680 (the smallest page the window allows), on Clips, the filter popover, the editor with a landscape and a portrait clip, Settings, the Games and Apps dialog, the More menu and About. Measure each state: the page never scrolls sideways, nothing sits past its panel, no label or button wraps, no two children of a row overlap, panels never overlap each other (the stacked editor once drew Export over the clip), and every text cut off in dots is one meant to be (a long name, the sandbox path).
   - Take screenshots of every state at 1920 x 1009 and 1264 x 680 at least and look at each one, plus zooms at scale 4 to 6 of anything about alignment, shadows, radii or spacing.
   - Use every control the round touched for real: selects through real mouse presses, typed times with Enter and with leaving the field, drags, the filter, the delete question, Export and Save, Save Cut, Restore Original, hotkey picking and the games search. The page console stays free of errors.
   - A media change (capture, audio filters, exports) gets a measured test on FFmpeg's own sources before it ships, never a guess.
7. Update this file for everything that changed: what it does now, the owner's picks and what they replaced, and the numbers measured.
8. Release only once the owner said publish; saying so for the round, or asking for the whole publish flow, counts:
   1. `git pull` again.
   2. Publish to a scratch folder with `-p:BaseOutputPath=<folder>\`, then take the screenshots as under Screenshots from that exe copied alone into an empty folder, which is also the release's lone exe test.
   3. Update the README to match what shipped, one short plain bullet per feature.
   4. Bump `<Version>` to the next patch (`1.8.5` after `1.8.4`) unless the owner names another, and publish again.
   5. Commit everything as one short Title Case line with no body and no attribution, and push to `master`.
   6. Sign the exe: `openssl dgst -sha256 -sign ~/.letsclip/update-signing.pem <exe> | base64 -w0 > <signature file>` in Git Bash. Then `gh release create v<version> <exe> --title "LetsClip v<version>" --notes-file <signature file>`: the release body is that one line and nothing else. Check that `releases/latest` redirects to it, that the asset's `digest` matches the local exe and that the body verifies (`Docs/Release.md`, Signed Updates). An update without a valid signature is refused by every install from v2.0.4 on.
   7. Delete the previous release with its tag (`gh release delete <tag> --yes --cleanup-tag`) and the local tag (`git tag -d`).
   8. Install it: stop the owner's running copy, copy the exe into `%LocalAppData%\Programs\LetsClip` through an `explorer.exe`-started `.cmd`, delete the older exes there, start it the same way, and have that script confirm the Run value points at the new exe and its SHA-256 matches the release.
   9. Clean up the install folder, the sandbox and any stray `ffmpeg.exe` from tests. A session cannot remove `C:\LetsClip` (the safety check refuses it), so ask the owner to delete it.
9. Report in a few lines what changed and anything left for the owner.

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

The notes behind these, the screenshots and the update flow are in `Docs/Release.md`.

## Where Data Lives

| What | Where |
|---|---|
| Settings (`UserSettings`) | `%AppData%\LetsClip\settings.json`, written through a temporary file flushed to disk and a rename |
| Clip metadata (trim, recorded time, frame rate, resolution, size, given name, game) | `%AppData%\LetsClip\Clips\<hash of path>.json`, ignored when the file's size or write time no longer match; metadata from before the resolution was kept gets it from one probe when listed, keeping its name, game and trim (`ClipLibrary.AddSizeAsync`) |
| WebView2 profile | `%AppData%\LetsClip\WebView2` |
| Replay cache | `%LocalAppData%\LetsClip\Cache\<session guid>\seg_*.ts` and `aud_*.ts` |
| Thumbnails and timeline strips | `%LocalAppData%\LetsClip\Thumbnails`, named by the file's size and write time so a replaced file never shows old pictures (`PicturePathOf`, older ones go when a new one lands), deleted with the clip's metadata when the library's watcher sees the clip deleted, in the app or in Explorer; the Games and Apps icons sit there too (`icon-<path key>.png`, drawn once by `DesktopShell.IconUrl`) |
| Logs | `Documents\LetsClip\Logs`, one file a day at Debug, the newest 30 kept (`maxArchiveFiles`) |
| Saved clips | `Videos\LetsClip` (the Videos known folder) or the folder picked in Settings; the Clips page reloads on every `ClipSaved` and on the folder watcher |
| Exports | `<clips folder>\Exports` |
| Originals of cut clips | `<clips folder>\.Originals\<same file name>`, a hidden folder, kept only with Delete Originals When Cutting off, removed when its last original is restored or all at once from Settings; recycling a clip recycles its original too |
| The exe | `%LocalAppData%\Programs\LetsClip\LetsClip_v<version>.exe`, wherever it was first run from: a Release start outside a sandbox from any other folder copies itself there, starts that copy with the same arguments and exits (`Updater.MoveToInstallFolder`), and updates download there (`AppInfo.InstallDirectory`). The downloaded file stays where the person put it |
| Start with Windows | `HKCU\...\Run` value `LetsClip` with `--tray`, and `Start Menu\Programs\LetsClip.lnk`, or `Programs\01 Apps\LetsClip.lnk` where that folder already exists (the owner's machines), both rewritten on every Release launch, on a pool thread (`App.Start`), since the shortcut's `WScript.Shell` write took 0.3 to 0.84 s on its first call and held the startup thread |
| The unpacked app | `%Temp%\.net\LetsClip_v<version>\<id>`, 432 MB per version (the .NET single file host's extraction, FFmpeg included). Windows never removes it, so five old versions held 2.2 GB on the i5 on 9 November 2026; every Release start now deletes every other `LetsClip_v*` extraction in the background (`Updater.DeleteOldUnpackedVersions`), renaming each first, since Windows refuses to rename a folder with an open file, so a copy still running keeps its own. Skipped in a sandbox and when the exe is not under `.net` |

A Debug build uses `LetsClip Debug` for all of these (`AppInfo.FolderName`) and never registers with Windows. `LETSCLIP_ROOT` moves every one of them under a scratch folder, gives the app its own single instance handle and skips the registration, for tests.

## Looks Wrong, Is Not

- Each capture writes two segment streams (`seg_` video, `aud_` audio); the save joins them by their shared timestamps.
- `ClipSaver` writes the clip as `.LetsClip-<guid>.mp4` and renames it only after it probes back; such leftovers after a crash are ignored by the library and never deleted, since the clips folder belongs to the user.
- `CacheFolders.Delete` refuses anything but a GUID folder directly under the cache root and only deletes files named like segments or x264 pass logs.
- The preview plays with sound while recording, so its sound lands in the next clip, the owner's pick since clips open unmuted (it used to start muted and warn on unmuting); with Only Record Game and Discord it stays out, since WebView2 plays in LetsClip's own process tree. `letsClip.setVolume` sets the video's `muted` and `volume` itself: a `muted` attribute only mutes an element the HTML parser made, never one Blazor adds, so before v1.3 the preview played sound behind a muted icon.
- Leaving the editor cancels a running export. Exiting leaves a running export to the kill-on-close job, like a crash.
- The tray balloon can open another clip into the open editor: each loaded clip has its own lifetime (`_lifetime` token, its own `DotNetObjectReference` let go on detach, its pending trim written when it ends), so a probe or timeline strip still running for the last clip never lands on the next, and `ExportPanel` is keyed by the clip's path, so an export or a pick never carries over.
- A saved clip holds up to a second of keyframe pre-roll below zero behind its MP4 edit list: `ffprobe -show_packets` lists it with negative times and the discard flag, players never show it.
- A lossless cut or Off export of a clip with B frames (`has_b_frames` 2, a file copied in from another recorder, or the x264 test clips) keeps up to two frames past End: a stream copy ends on decode order, and the MP4 edit list then shows the frames whose times run past it (470 frames for a 7.8 s cut of a 60 FPS clip against 468, measured on 9 November 2026; its first 468 matched exactly). LetsClip's own recordings never have B frames (`-bf 0`), so their cuts are frame exact, and an export for a Discord plan re-encodes and is exact either way.
- `ExtendedLimits` (`FFmpeg`) and `AdapterDescription` (`MonitorCatalog`) are explicit layouts holding only the fields read, at their 64 bit offsets, like `ModeInfo`; the app is x64 only. Checked after the change: children die with a killed parent and the AMD adapter is still found.
- A capture's FFmpeg is disposed only after its exit code is read (`SegmentProcess.Exited` always completes), so `ReplayRecorder.WatchEndAsync` never waits forever; `GameWatcher` reads and lets go of the found game's handle under one lock.
- A still game window's clip can end its video before its audio: the last frame holds while the sound runs on to the press.
- A warm-up clip's file starts with about a second of audio before the first video frame, and that first frame can show content older than its timestamp (`ddagrab` probes it at start); thumbnails come from a second in.
- The video FFmpeg holds about 600 MB of private memory, flat over a 10 minute run; the audio one about 23 MB.
- Opening the monitor list shows a `MonitorOverlay` on every monitor (accent edge and name on the picked one, grey on the others), and a pick stays outlined 1.5 s. They are placed and drawn at zero opacity, then all made visible in one dispatcher pass, since shown one by one the second monitor lit up a moment after the first. The overlays are click through, never activate and carry `WDA_EXCLUDEFROMCAPTURE`, so a screen grab or a clip never shows them even while buffering.
- The playhead draws above the trim handles at full height, so it never thins to half its width at the start or end; it is as wide as the grip (`--grip-width`) and its colour (`--grip`), centred on its time.
- The trim handles sit outside the kept part, their inner edge on the time, so two handles never cross or overlap; the timeline pads both ends by a handle's width (`--handle-width`) and maps time over the inner `timeline-track`. The strip runs the timeline's whole width, under the handles too, and the outer shades stretch the same handle width, so no grey end shows without a picture; Every part of the frame is a whole number of screen pixels at any Windows scale, the owner's rule that it looks the same at every width: `--device-pixel` (one screen pixel in CSS pixels, set by `AppShell.js` from `devicePixelRatio` and renewed through a narrow `matchMedia` resolution range, since the ratio is rarely an exact decimal) sizes the grip's gaps, width and height, the bars and the handles with `round()`, and positions round to it too (`percent` in `AppShell.js`, `ClipEditor.Percent`), so all parts share one sub-pixel phase and snap alike. At 125% the grip measured 6 | 4 | 6 screen pixels across at every position, where whole CSS pixels gave a grip of 3 or 4, and 26 | 28 | 26 down since it grew to 22 px (28 | 22 | 28 at 18 px); emulated across, 8 | 5 | 8 at 150%, 9 | 5 | 9 at 175%, 10 | 6 | 10 at 200% and 5 | 3 | 5 at 100%. The frame is padding and a spread, never borders: Chromium floors border widths to screen pixels, so an inexact ratio such as 1.0000000149 would lose one. CDP's emulation re-evaluates media queries only when an override starts or ends, and its screenshots of a scale it emulates are resampled, so check a scale through a cleared override and real pixels at the window's own scale. Dragging and arrow keys run in `AppShell.js`: it reads the range from the handles' `data-time`, paints handles, shades and selection itself, seeks the video, and tells Blazor once (`OnRange`) when a drag ends or a key steps, so no frame waits on a render. Blazor's next render matches what the script painted.
- Play runs from the playhead and stops at the end point, starting over from the start point once there; the 2 second jumps clear that stop when they pass it.
- The timeline strip decodes keyframes only, one a second, so it samples 12 frames over the duration less one second; spread over the whole clip, the last tile of a short one stayed black. A copied clip with sparse keyframes (OBS, x264's default 250 frames) runs out early, so `tpad` repeats the last one and no tile stays black.
- A clip with no metadata reads its recording time from its generated name (`ClipNames.DateOf`), so a copied file keeps its date.
- WebView2's browser keys, zoom and pinch zoom are off (`AreBrowserAcceleratorKeysEnabled`, `IsZoomControlEnabled`, `IsPinchZoomEnabled`).
- The BtbN FFmpeg build prints onnxruntime's CPU core lines on standard error before anything else. The capture's error reader skips lines that do not start with `[`, and `FFmpeg.RunAsync` keeps only the last error line, so they never become a reason.
- A cut clip's file is shorter than its hidden original and its write time is new, so a cut clip sorts by its recorded date (metadata), never by the file's time.

## This Project's Working Agreement

The owner's standing rules from these rounds, on top of the shared `CLAUDE.md`:

- Everything has to be consistent across the whole app and across systems: a remark about one place is a rule for every place. Pixel perfect means measured: margins, paddings, sizes, baselines, shadows.
- A handoff or outside audit does not override these rules: no branches, no test projects, the owner decides when to publish.
- The demo video never scrolls: the owner saw the Settings scroll as jittery and weird. Show the page and stop, or do something else.
- Off in Fit for Discord means exactly what was recorded, without any loss. Any encode belongs to a Discord plan that needs it.
- Hard no, tried and removed: Next and Previous clip buttons, editor keyboard shortcuts (a click on the clip to play or pause stays), the size beside the clip count, a bitrate chip on the thumbnail, the verdict chip beside Bitrate, the info button on Size Limit, the words On or Muted beside Audio. Not wanted: multi select, favourites, looping the selection.
- Wanted and kept: the hover preview, Sort Clips, the clip facts in the info tooltip, Disk Space in Settings, record mode links on the capture panel, automatic healing.
- A new look (an icon, a colour, a glyph) is shown first as a few designs side by side, rendered in the app at real size and enlarged, for the owner to pick; then applied exactly as picked. Icons are solid, never see-through, and measured centred.
- Animations: the crossfades are liked at 150 ms. Do not slow them. Never flash a spinner on a quick load.
- This session type: the Claude app can start a session in a worktree whose folder is empty, and a hook then refuses edits to the main checkout. Recreate it with `git worktree add --detach <path> master`, copy `LetsClip.Desktop\FFmpeg`'s binaries in, apply the main checkout's uncommitted diff (`git -C main diff --binary | git -C worktree apply`, plus untracked files), `git add -A` there so only this round shows as unstaged, work and test there, then carry it back the same way. `git worktree remove --force` afterwards leaves only an empty folder the session holds open. The main checkout's changes stay unstaged.
- Force killing the owner's copy can leave AMF stuck for the next start, so the install script waits 10 s between the kill and the new exe.
- Never do anything with hotkeys, the owner's rule of 9 November 2026: no mouse buttons, no screenshot key, no second save key, no change to how the save key works. The one change since, asked for by the owner on 11 November 2026: a press while a save is running does nothing, so spamming the key saves one clip.
- Ignore HDR, the owner's words: it stays blocked with its message, nothing is built for it.
- A Release exe started outside a sandbox installs itself: it copies itself into `%LocalAppData%\Programs\LetsClip`, rewrites the Run value and shortcut, and registers a crash restart with Windows. Test builds outside a sandbox get another version number (`-p:Version=0.0.1`), or they overwrite the owner's installed exe of the same name; back up the Run value, the shortcut and `settings.json` first and restore them after.
- The session's Edit tool is refused on the main checkout but the shell is not: `HANDOFF.md` and carried diffs go in through shell commands.
- The update signing key is `C:\Users\yusuf\.letsclip\update-signing.pem`, outside every repository. Never commit it, never upload it, never print it. Without it no release can be signed and installs stop updating themselves, so the owner keeps a copy somewhere safe (backed up to his iCloud Drive on 11 November 2026, checked identical).
- The .NET 11 SDK is installed machine wide since 11 November 2026 (`winget install Microsoft.DotNet.SDK.11`, the owner's own admin prompt), so plain `dotnet` builds the solution. A session cannot answer that prompt: a missing SDK goes into a user folder with Microsoft's `dotnet-install.ps1` and `DOTNET_ROOT` until the owner installs it.
- Updates may restart LetsClip in the background, the owner's pick: losing the unsaved replay to an update does not matter.
