# LetsClip, Recovery

What heals itself and what the person sees, with the research and injected failures behind it. Read before touching recovery, retries, encoders or anything that can fail; add every new failure to the table. Part of `CLAUDE.md`, its rules bind the same way.

## Recovery

The owner's rule: consistent on every kind of system, and anything that can fix itself does so without the person noticing. Only what lasts or what they alone can fix is ever shown. Add every new failure to this table.

| What goes wrong | What happens | What the person sees |
|---|---|---|
| A hardware encoder's capture hangs or fails to start | The same encoder gets a second start (12 s limit each), then the next one | Recording throughout |
| Recording runs on a lesser encoder than the machine has | `UpgradeAsync` tries the better one after 1, 2, 5 and 15 minutes, then every 30: it runs beside the running session until it holds a whole replay, then takes over, so no history is lost and a failed try costs nothing | Nothing |
| A window's encoder hangs, at the start or mid session | A hung encoder and a still window both send nothing, so after 20 s of no video segment `ProbeWindowAsync` starts a fresh capture beside it, on the same encoder and then x264, at most once per quiet spell. Only a fresh capture that draws while the old one still sends nothing takes over; a still window keeps its session and history | Recording throughout |
| A session dies or stalls | Retries after 2, 5, 15, 30 s, then each minute | Recording for the first four in a row, about a minute, then the error with Try Again while retries go on |
| HDR on, less free space than 2 GB plus the whole replay, FFmpeg missing, monitor gone (`CaptureBlockedException`, `CaptureFailure.Blocked`) | Retried the same way | Shown at once, only the person can fix these |
| An audio device is pulled or restarts | Reconnects, falling back to the default | A Notice only after 5 s of it (`ProblemTicks`) |
| Capture falls below its frame rate | Nothing to do | A Notice only after 10 slow reports (`SlowTicks`), never for a loading screen |
| A save hits an IO error (a scanner holding the file) | Saved again after 1 s | Nothing |
| The clips folder is gone or read only | `ClipSaver.FolderFor` saves to `Videos\LetsClip` | The balloon opens that file |
| An export encoder hangs or fails | The next encoder takes over | Progress carries on |
| A page throws | `SelfHealingBoundary` logs it and goes back to Clips, waiting for the route so the broken page is not rendered again; a second failure within 10 s shows the panel, so it can never loop | Clips |
| WebView2's renderer crashes | `ProcessFailed` reloads the page, recording never depended on it | A blink |
| WebView2's whole browser process exits | `ProcessFailed` with `BrowserProcessExited` runs `MainWindow.RebuildPageAsync`: the dead page is released without waiting on it (`ReleasePageAsync(browserGone: true)`, the control disposed directly) and a new one is built at once while shown, else on the next show. Tested with CDP's `Browser.crash`: the Clips page was back with its cards in 1.2 s, the window responsive, no background errors; waiting on the dead page's own disposal took 5 s and logged 34 unobserved `The WebView control is no longer valid` errors from Blazor reporting to the dead browser, which is why it no longer waits | A blink |
| An update is out while LetsClip sits in the tray | Installed after 10 minutes hidden with nothing saving or exporting, the new version starting in the tray (see Updates) | Nothing, the replay starts over |
| Temp cleaners or Storage Sense sweep `%Temp%` | FFmpeg sits in the single file exe's extraction under `%Temp%\.net`, so `FFmpeg.HoldFiles` keeps every file open from start to exit | Nothing |
| The microphone test loses a device | It ends and says so | A Notice |
| The clips folder's watcher overflows or its drive leaves | `ClipLibrary` rescans and builds a new watcher, 15 s between tries while the folder is missing (`Rewatch`) | Clips Folder Not Available until it returns, 4 s after a test drive came back |
| A window's encoder hangs | Covered above, the fresh capture needs no restart of the game | Recording throughout |
| LetsClip itself crashes or hangs | `AppDomain.UnhandledException` logs it as Fatal (`Crashed`), and Windows restarts it in the tray (`RegisterApplicationRestart` in `StartupRegistration.Apply`, crashes and hangs only, Release outside a sandbox, only after 60 s of running). The restart carries `--after-update <pid> <own path> --tray`, so it waits for the crashed process, which still holds the single instance handle while Windows writes its report. Measured: back 5.1 s after the crash, no click | Nothing, the replay starts over |
| An update throws while starting | `App.OnStartup` starts the version it replaced again (`Updater.Launch`), which deletes the broken exe, rewrites the Run value and skips that release from then on (`UserSettings.AttemptedUpdate`). Measured: the old version was back 0.15 s later | Nothing |
| An update dies before it can log | The old version wrote `AttemptedUpdate` before launching it, so the next start of the old version skips that release instead of installing it at every sign in | Nothing records until the next start, then Recording |
| A timer or thread body throws something unlisted | Caught and logged where it happens: the clip folder timers (`ClipLibrary.Guard`), the game check, the Discord watch, the audio writer, the save chime's output meter | Nothing |
| No microphone or output device exists at all | Automatic records silence and connects when one becomes the default | `No Microphone Detected` in Settings, no warning |
| Old versions' unpacked folders pile up in `%Temp%\.net` | Deleted at the next Release start, both the versioned `LetsClip_v*` folders of v2.0.9 and older and the other builds under `LetsClip`, a running copy's own folder kept | Nothing |
| The clips drive fills up | A save first checks its segments plus 50 MB fit (`ClipSaver`) and refuses at once, never retried; Settings shows Almost Full beside Disk Space under room for three replays or 1 GB | The falling chime and `The clips drive is full, free some space` |
| The GPU route will not start (a driver refusing `scale_d3d11`) | The same encoder runs on the CPU route, the next start tries the GPU first again | Recording throughout |
| A clip is in use while it is renamed or moved | A rename retries five times a second apart (`ReplaceWhileBusyAsync`), then keeps its old name and title; a clip named before v2.0.10 whose file is held at the first start is renamed at a later start (`NameFilesAsync`); a folder move leaves that file in the old folder and counts it | A warning snackbar |
| A newer LetsClip starts while an older one runs | The newer one renames the running exe aside, signals it to exit (`{FolderName}.Exit`), takes `LetsClip.exe` and starts from there (`Updater.TakeOver`); an equal or older one only shows the running window | Nothing |
| An update cannot replace `LetsClip.exe` (held by a scanner) | The download starts under its versioned name and logs `Failed To Swap The Installed Exe`; the Run value and shortcuts follow it and the next update tries again | Nothing |
| What an update changed cannot be read (offline, GitHub's rate limit) | Nothing is shown and the next start asks again (`Updater.ChangesAsync` returns null) | Nothing |
| WebView2 is not installed | LetsClip says so before anything starts, OK opens Microsoft's download, and exits | The message |

## Reliability Research And Tests

Done on 8 November 2026 after `Using x264 on the CPU, AMD AMF failed` appeared. Keep the conclusions; rerun the tools below whenever capture, encoders or recovery change.

- What happened: the installer step force killed the owner's copy mid recording and started the new exe 3 s later. Its encoder test passed in 1 s, then the real capture produced nothing for 12 s and fell to x264. The log held no FFmpeg error, which is why starts now log which half went quiet.
- It could not be reproduced: 140 cycles of starting `ddagrab` into `h264_amf` and killing it, restarting at once or after 3 s, and 20 with a graceful `q`, gave 0 hangs, first frame at 590 to 720 ms, the single failed run never repeating. So a graceful stop was not added, and nothing depends on knowing the cause: two starts plus `UpgradeAsync` cover it either way.
- AMD: no public report of AMF hanging after a killed process was found; an OBS issue shows AMF deadlocking at shutdown on one preset, fixed by a newer driver. AMF refusing `qvbr` on old cards would fail `WorksAsync` and fall to x264.
- NVIDIA: consumer cards cap concurrent NVENC sessions (2, then 3, then 5 in the support matrix), so OBS or another recorder can use them up. A start then fails, x264 takes over and `UpgradeAsync` moves back once a session frees up. NVENC is still untested on real hardware here.
- Intel: on hybrid machines `h264_qsv` can fail with `unsupported (-3)` when the Intel adapter drives no display, so it falls to the next candidate.
- Hybrid laptops: Desktop Duplication returns `DXGI_ERROR_UNSUPPORTED` when run on the GPU that does not drive the display; `MonitorCatalog` passes the adapter that owns the output, so this is already handled. FFmpeg ticket 10385 (ddagrab on non primary outputs) was fixed by 7.1.1 and we ship 9.0.2.
- Software rendering: `backdrop-filter` is slow without a GPU in some engines, Chromium 140 handles it. Capture needs a GPU anyway, so nothing was changed.
- Injected failures, through the stand-in `ffmpeg.exe` below, on a window session with AMF: a hang on the first capture, three failed rounds of all encoders, the audio process dying at 6 s, the encoder test hanging, the video process dying at 35 s and a hang 10 s in all stayed on Recording with no error shown. Every encoder failing keeps Recording for 55.8 s, four quiet retries, then shows the error. A hung window capture moved to a fresh one 22 s after the hang, where before it read Recording for ever and a save said `Nothing has moved in the window yet` over a moving window. A window still from its first moment kept its session, with both probes hung or not. A save pressed during retries waits and saves what the new session holds (1.3 s of it, 12 s later).
- Cost with the window open on the 7800X3D, Debug: Clips idle 0.5% of a core and WebView2 0%, the hover preview 5.9% and 27% only while it plays, Settings idle 0% and 0.2%, the live microphone test 2% and 1.4% plus 0.5% for its FFmpeg.
- A probe of a still window takes 8 s per encoder with x264 and 12 to 30 s with AMF. The cause was not found, it costs one idle FFmpeg once per quiet spell.
- Scales: Clips, Settings and the editor measure clean (no sideways scroll, nothing past its panel, no side panel scroll) at the page sizes Windows gives 1080p at 100, 125 and 150%, 4K at 200%, 1440p at 100% and 768p at 100%.
- Locales: names, dates, durations and typed times round trip under en-US, nl-BE, de-DE, tr-TR, ar-SA, fa-IR, th-TH, ja-JP, he-IL, hi-IN and fr-CH. Arabic and Persian use `٫` as the decimal separator, so `Formatter.ParseDuration` now reads the culture's own.
- Paths: list, hover preview, editor, Save Cut, export and restore all work with the root in a folder with accents, apostrophes, Japanese, `#`, `&` and `%`, and a clip named with those.

Tools, all throwaway and never committed:

- CDP helper: Node over `http://127.0.0.1:9333`. The window may open on a narrow monitor where the page is zoomed (`devicePixelRatio` 0.9 on the portrait monitor), so read `cssVisualViewport.zoom` from `Page.getLayoutMetrics` and multiply sizes and screenshot clips by it, or every crop lands in the wrong place; mouse positions stay plain CSS pixels, multiplied they land a tenth off.
- Per frame smoothness: inject a `requestAnimationFrame` recorder of spinner, strip, panels, handles, held frame and video readiness, press the action, count frames that would flash (must be 0).
- Consistency dump: walk every visible element on every screen and group computed `box-shadow`, `filter`, radius, font and button sizes. Exactly two shadow values are allowed, plus the hover ring, the info icon's `drop-shadow` and `--picture-shadow`.
- Failure injection: a small .NET file-based `shim.cs` built as `ffmpeg.exe` beside `ffmpeg.real.exe` reads `faults.txt` lines (`pattern|video,audio,test|hang,crash,die|ms|count`; `hang` with `ms` above 0 runs the real one that long, then `NtSuspendProcess`), else runs the real one in a kill on close job. A harness on `CaptureController` records a separate `pattern.exe` window keyed as the only game (a game cannot be the harness's own process, `GameWatcher` skips itself) and prints every change of `StatusText`, `ShowsError` and `IsRecording` per scenario.
- A page exception is tested with a temporary `@page "/crash"` that throws, removed before the diff is carried over; a renderer crash with CDP's `Page.crash`.
- Monitor mode without recording: rename the build's `FFmpeg\ffmpeg.exe` for the look.
- The owner's G522 gives only silence to a process started from the Claude app, so the microphone path is proven with synthetic speech; the owner confirms by ear.

## Injected In A Live Match, 11 November 2026

The gaps the handoff's round 3 closed were exercised through a stand in `ffmpeg.exe` (a throwaway that hangs or fails when a file names a pattern in its arguments, else runs the real one) under a windowless harness on `CaptureController`, recording Overwatch with the owner's settings.

- A failure streak long past counts anew: with `_failures` set to 9 and the last failure six minutes old, killing the video encoder 4 s into its session logged `Failure 1 In A Row, Brief, Out Of Sight`, kept the chip on Recording and was recording again 3.3 s later. The control, the same nine failures but recent, showed Recording Error at once and waited 60 s, which is what every later failure did before the fix.
- A failed encoder test is not run again for that encoder's second start: with the AMF test failing once, the log holds one `Skipped AMD AMF`, then the x264 test, and recording on x264 4.9 s after launch.
- A start throwing an unlisted exception was not injected, no way to make FFmpeg cause one was found; the catch is now unconditional.

## The Game Window Crash, Found 13 November 2026

The owner's video FFmpeg died with `0xC0000005` seven times between 8 and 12 November, always on Overwatch's window with AMF, after 4 to 43 minutes, with no warning first. Reproduced outside the app: a full screen test window on the first monitor that animates and, every 9 s, minimises, bursts 40 random sizes, hides, shrinks to 1 x 1, goes windowed and moves, captured by the app's own command lines under a small debugger that records the faulting module and writes a minidump (`LcCrash`, started through Explorer so it outlived the session).

- Window capture with AMF crashed after 29.8 minutes, in a resize burst. Window capture with x264 and monitor capture with AMF ran two hours each without one.
- The faulting thread belonged to AMD's D3D11 driver itself (`amdxx64.dll` 32.0.32015.7029, `+0x5EC80`, reading address 0x8): every frame on its stack was the driver's own, down to the thread start. FFmpeg's filter thread was inside `hwdownload` and AMF was encoding at that moment.
- The difference between the crashing setup and the two that held: FFmpeg hands an encoder the one D3D11 device it finds, so `gfxcapture` and AMF shared the capture's device (`Using device capture (type d3d11va) with h264_amf encoder`). `ddagrab` makes its own device, and x264 takes none.
- Fix: a window capture initialises a second, unused device (`-init_hw_device d3d11va=spare:N`). With two devices of a type FFmpeg gives the encoder neither (`hw_device_get_by_type` returns none when two match), so AMF makes its own, like on a monitor. The log then shows no `Using device capture` line.
- Soak, 14:47 to 16:12 on a fresh stress window: the shared-device baseline crashed twice (after 73.9 and then 5.7 minutes), both times at the same `amdxx64.dll+0x5EC80`, while the two separate-device copies beside it on the same window ran 1 hour 25 minutes each without a crash. Counting the first run, sharing crashed three times in about 5 hours 10 minutes and separate devices never in 2 hours 50 minutes. Shipped in v2.0.9.
