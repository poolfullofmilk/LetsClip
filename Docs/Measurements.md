# LetsClip, Audits And Measurements

The dated record: audits, sweeps and field tests with their numbers and throwaway tools. Read when a round needs a number to compare with, or before repeating a test. Add each new measured round at the end.

## External Audit, November 2026

A handoff of 27 findings against v1.8.7, from a Linux review that could not run the app. Every one below was checked on Windows with `LETSCLIP_ROOT` sandboxes, throwaway file-based apps and CDP on the real app.

| Finding | Result |
|---|---|
| F01 restore overwrites a replacement | Reproduced, fixed: `ForeignOriginal`. B stays byte identical, A stays kept, cutting B then restoring gives B back |
| F02 lost metadata updates | Reproduced, 300 of 300 rounds lost one; fixed, 0 of 300 |
| F03 cut and restore commits | Fixed with `File.Replace`, a failed step puts the source back, a locked file fails with the source intact and no leftovers. Power loss between steps not simulated |
| F04 microphone test outliving its page | Fixed with a start generation: a double press and leaving mid start end with no test |
| F05 export acting on the wrong clip | Fixed: the cut is bound to the exported path, and Save Cut, Restore and Delete are disabled during an export, Export during a swap |
| F06 mixed trim inputs | Fixed: the handles hold the truth, the keyboard cache is gone |
| F07 playback keeps the old end | Fixed: an active play follows the end point |
| F08 very short clips | Fixed: shorter than one step is whole clip only, never a negative range |
| F09 rounding differs | Reproduced, fixed: halves round up in C# and script |
| F10 view transitions missing | Fixed: the change still happens |
| F11 stale pictures | Reproduced, fixed: names carry the file's generation |
| F12 malformed settings | Reproduced, fixed: bad games and enums dropped at `Normalized` |
| F13 Windows device names | Reproduced, fixed: `CON` becomes `Clip CON`, names stop at 120 characters |
| F14 audio stop paths | Fixed: the filter dies first, stopping with a suspended filter took 41 ms; a lost microphone ends the test; a failed start disposes its recorder. Real unplugging not run |
| F15 work outliving its owner | Fixed: six quick open and close rounds, nothing unobserved, no FFmpeg left. Semaphores are left undisposed on purpose, late waiters then finish quietly |
| F16 readers after a kill | Fixed: readers are drained, cancel and timeout each return on time |
| F17 lost watcher, unavailable folder | Fixed, tested with `subst` |
| F18 stale device lists | Fixed: `AudioDevices.WatchChanges` with a 400 ms settle. Real plugging not run |
| F19 frame rate from the wrong monitor | Fixed: a window on the 165 Hz monitor with 240 picked records at 165 and offers up to 165 |
| F20 keyboard layouts | Fixed: letters follow `event.key`, others the physical key; Escape restores |
| F21 one bad manifest, same named exes | Fixed: per manifest, and an added exe must match its product name, a same named other program is not recorded. Two exes without a product name still match |
| F22 monitor badge contrast | Fixed: black on the light blue, about 13:1 |
| F23 smallest window | Fixed: the player may shrink to 96 px, at 501 px of page height everything is reachable |
| F24 keyboard info | Fixed: the info icon takes focus, shows its tooltip and has a spoken label |
| F25 reduced motion | No change, this is the policy: nothing moves, slides or pulses, short opacity changes stay since they are not motion, and the preview checks the setting each time |
| F26 update cleanup | Fixed: see Updates, tested with disposable files |
| F27 test project in CI | Declined: the owner's rule is no test projects, self tests live beside the logic and were extended |

Package audit on Windows: no vulnerable packages, direct or transitive. Not run: real device unplugging, Windows DPI changes between 100 and 200%, NVENC and Quick Sync on this PC, the 10 to 1,000 clip performance table and a 30 minute soak.

## Whole Solution Ponytail Audit, 9 November 2026

Before v2.0.2 every tracked file was read, from the csproj files and the workflow to every page, component, service, helper, the stylesheet and the page script, with scans for names used only where they are declared, CSS classes no markup uses and page script functions nothing calls. Almost everything earns its place: the capture recovery, the P/Invoke layouts and the measured media rules stay. What went: `Formatter.FormatMegabytes` (`FormatSize` of the same bytes reads the same), `CaptureController.AddGame` (`SetRecorded` with one row, also used by the tray balloon and Browse for an Exe), the adapter lookup every caller of `VideoEncoders.Candidates` passed in (it reads `MonitorCatalog.AdapterVendors` itself now, six call sites shorter), a single use `StripPathOf`, two `Color="Color.Default"` attributes that were the default, and repeated default literals in `UserSettings.Normalized`, which now takes each bad value's default from one `new UserSettings()`; that also fixed a frame rate from a damaged file falling back to 30 FPS where the default is 60 (checked with a file of bad values: 60, 30 s, 1080p, Normal, Newest, Off and a 0.4 volume came back).

A second whole solution audit, before v2.0.3, followed the feature sweep the same evening at the owner's maximum depth: every file read again, plus a script listing every C# member, CSS class, CSS custom property and page script function named only where it is declared. That scan found nothing dead, only the P/Invoke struct fields the layouts need, MudBlazor's own `active`, and `field-warning` and `field-error`, which `FieldDetail` builds from its colour. What went:

| Cut | Now |
|---|---|
| `ClipLibrary.CutAsync` and `ClipExporter.CopyAsync` each built the same stream copy and probed it | One `ClipLibrary.CopyRangeAsync(source, range, streams, output, token)`, the cut passing `-map 0`, an export the video and maybe the audio |
| `Path.Combine(folder, $"{PartialPrefix}{Guid.NewGuid():N}.mp4")` in four places | `ClipLibrary.PartialPathIn(folder)`, used by saves, cuts and both export kinds |
| `.mud-alert .mud-alert-message` declared twice, 20 lines apart | One rule |
| `CaptureController.KnownGames`, one caller | Inlined into `ApplyGames` |
| `MainWindow.HideWhenMinimized` and `PlaySaveChime`, one subscription each | Lambdas at their events |
| `ClipLibrary.ThumbnailPathOf`, a wrapper | `PicturePathOf(path, ThumbnailSuffix)` beside `StripSuffix` |
| `CapturePanel.QualityLabel`, `ToString` by another name | `quality.ToString()` |
| Defaults on `ClipInfo.Monitor`, `WrittenUtc` and `MediaInfo.AudioBitsPerSecond`, `FrameTimes` that their one constructor call always overrode | No defaults |
| The theme's `Overline` text transform, a variant nothing renders | Gone |
| A stray `Shared Radii` comment, `.mud-switch-track` between `.mud-chip` and its children, the switch padding rule inside the short window media block, the full screen control rule under `.player:has`, the transport rule after its children | Each under its own selector; the `Clips` region now reads `Clips And Dialogs` |

Kept on purpose: the P/Invoke fields, `AudioDevices.Playback` and `Microphones` (they keep NAudio's types out of the page), the shared theme palette copied from the sibling apps, `ClipLibrary.AddSizeAsync` (metadata from old versions still needs it), `YesNoDialog.ConfirmCutAsync` (its wording belongs with the dialog), every component with one use that renders on its own schedule (`CaptureChip`, `PlayerClock`, `LevelMeter`), and everything about hotkeys, the owner's rule.

Checked after the cuts in a Debug sandbox with the test clips, a 30 MB noisy 1080p60 clip and a 110 MB 1440p60 one: the CDP sweep at all six sizes over Clips, the filter popover, the editor with a landscape and a portrait clip, Settings, the Games and Apps dialog, the More menu and About found no sideways scroll, nothing past its panel, no overlap, no wrap and only the sandbox path in dots, with no console error. Real presses on the Quality select (High warned, Normal came back) and Fit for Discord (Nitro Basic and Nitro disabled for the 110 MB clip's 15 s), typed times with Enter and with leaving the field (`4,5s` and `12,3s`, both on frame starts), a handle drag (4.683 s, a frame start), the delete question's No, Save Cut, Restore Original, an Off export, Export and Save on Free, Copy File and minimising to the tray all worked. Measured: the restored file kept again by the next cut hashed identical to the source, the Off export's 1800 video packets matched the source's one for one, the Free export came out 18.2 MB at 1920 x 1080, 60 FPS and 15.000 s, and a clip without B frames cut losslessly to exactly the frames of its range (468, 186 and 300 frames for three ranges).

## Feature And Speed Sweep, 9 November 2026

The owner asked for a sweep of features, improvements and speed. Every source file was read again, competitors and the libraries were researched (Medal, NVIDIA App, AMD Adrenalin, OBS, Insights, Outplayed; FFmpeg, BlazorWebView, NAudio, Discord), and the biggest ideas were measured. What came of it, with the owner's picks:

| Idea | Outcome |
|---|---|
| Record only the game's and Discord's sound | Built, the owner's request: Only Record Game and Discord (see Capture) |
| Updates seen only when the window opened | Built: checks at launch, every 6 hours and on every open, installs itself after 10 minutes hidden (see Updates) |
| WebView2's browser process exiting needed a restart | Built: the page rebuilds itself (see Recovery) |
| Copy a clip without exporting it | Built: Copy File on every card and in the editor header |
| Clips page reading the disk per card per render | Fixed: URLs from `ClipInfo`, lazy thumbnails |
| Two ffprobe runs per editor open | Fixed: one probe with packet times |
| MudBlazor 9.12.0 and WebView.Wpf 10.0.130 | Updated, nothing else was out of date or vulnerable |
| The HWND page host, measured above | Deferred to .NET 11, the owner's pick (see Build, Run And Publish) |
| HDR capture (`ddagrab` and `gfxcapture` take `output_fmt` 8bit, 10bit or 16bit, so 8 bit SDR from an HDR desktop would be one test away) | Not wanted, the owner's words: ignore HDR |
| Mouse buttons as the save key, a screenshot key, any other hotkey work | Never, the owner's rule: nothing with hotkeys |
| A separate microphone track, manual long recordings, automatic clipping on game events | Not picked |
| AV1 or HEVC capture to save disk | Declined: Discord's playback of both is still mixed, and Off must stay an exact, shareable copy |
| `scale_d3d11` on AMD to skip the frame readback | Still blocked: no upstream FFmpeg fix found on 9 November 2026; GhostClip PR #3 hit the same `Could not create the texture` on an RX 6600 and fell back to encoding BGRA from memory, which this file already measured and declined for its colour |
| Discord's limits | Rechecked: Free 20 MB since 13 August 2026 (from 10 MB), Nitro Basic 50 MB, Nitro 500 MB on most pages and 1 GB on one, so 500 MB stays |
| The About text said one monitor | Now game, app or monitor; the GitHub description must be changed to match by the owner |
| `LetsClip.Desktop/FFmpeg/README.md` listed features from before window capture | Rewritten to every filter, encoder and format in use |

## Overwatch Field Test, 8 November 2026

The owner played Overwatch with the microphone on and talking while a harness on `CaptureController` stepped through the settings. Ryzen 7 7800X3D, RX 9070 XT, the game at 2560 x 1440 on the 240 Hz monitor, its own FPS counter reading 160 to 300, Medal recording the same game the whole time. Every number below is the game running, video FFmpeg in one core's share, encoder the GPU's video engine for that FFmpeg, frames kept of those expected in a 30 s clip.

| Setting | Video FFmpeg | Encoder | Frames | Clip |
|---|---|---|---|---|
| 1440p60 Normal | 51% | 18% | 1797 of 1800 | 18 to 27 Mbps |
| 1080p60 Normal | 44% | 21% | 1787 of 1800 | 25 Mbps |
| 1080p60 High | 41% | 21% | 1795 of 1800 | 30 Mbps |
| 1080p60 Max | 41% | 21% | 1795 of 1801 | 24 Mbps |
| 1440p60 High | 60% | 16% | 1797 of 1801 | 22 Mbps |
| 1440p60 Max | 54% | 15% | 1797 of 1801 | 31 Mbps |
| 720p60 Normal | 28% | 10% | 1799 of 1800 | 14 Mbps |
| 1080p30 Normal | 22% | 10% | 893 of 900 | 13 Mbps |
| 1440p30 Normal | 25% | 8% | 893 of 901 | 14 Mbps |
| 1080p120 Normal | 71% | 23% | 3330 of 3601 | 26 Mbps |
| 1080p144 Normal | 63% | 23% | 3265 of 4321 | scene dependent |
| 1440p120 Normal | 84% | 27% | 3054 of 3600 | scene dependent |
| 1440p144 Normal | 89% | 28% | 2862 of 4321 | scene dependent |

- Bits depend on the scene far more than on the setting: 30 s of a menu is 1 to 9 Mbps, a fight 18 to 37. Compare settings only on one recorded source. Overwatch draws its menus at 60 FPS, so a rate above 60 only means something inside a match: the first run of 120 and 144 FPS read 1786 of 3598 frames for that reason. Measure in a match, the harness waits for the game above 150% of a core first.
- At 60 FPS frames are complete in a match, 3 held of 1800 at best, a few at worst, a longest gap of 33 to 83 ms where the game itself hitched. Above 60 a window capture delivers what the game presents and the capture cadence allows, about 90% at 1080p120, so 120 and up stay amber.
- The rest: audio FFmpeg 1.7 to 3.3% with Noise Cancellation, 1.7% without it, 1.1% with no microphone; video FFmpeg 506 to 581 MB private, flat; the app itself under 1%; LetsClip's FFmpeg added 1 to 4% of the GPU's 3D engine and 0.5% of copy against the game's 57 to 91%.
- Medal beside it: `MedalEncoder` at 13 to 20% of a core and 12 to 18% of the video engine, recording HEVC 1080p at about 12 Mbps. LetsClip costs 2.5 times its CPU, only because it reads each frame back to the CPU (about 25% of a core at 1080p60, 3% of this CPU in all). Removing that needs a GPU colour conversion FFmpeg 9.0.2 cannot do on AMD (see Capture); the game held 160 to 300 FPS with both recording.
- The whole flow at 1440p with the game running: saves list and probe 2560 x 1440, 30.00 s, 0.8 to 1.5 s (2.6 s during an export); a cut keeps 2560 x 1440 and exactly 900 frames for 15 s; Restore gives back a byte identical file; exports while recording and across a capture restart come out frame exact; recording during exports held 1785 of 1800 frames against 1797 before, the only cost of sharing the encoder.

Quality, measured with VMAF on 5 s of raw 2560 x 1440 Overwatch (the raw NV12 from the capture chain itself), every sub-1440p result upscaled back with bicubic as a 1440p screen shows it:

| Encode | VMAF | Mbps |
|---|---|---|
| 1440p Normal | 95.3 | 25.4 |
| 1440p High | 97.1 | 30.8 |
| 1440p Max | 97.2 | 33.7 |
| 1080p Normal, bilinear (as shipped until v2.0) | 86.0 | 20.4 |
| 1080p High, bilinear | 87.3 | 29.1 |
| 1080p Max, bilinear | 88.0 | 31.6 |
| 1080p Normal, bicubic | 92.7 | 22.2 |
| 1080p Normal, Lanczos (now) | 93.4 | 22.8 |
| 720p Normal, bilinear | 70.6 | 13.6 |
| 720p Normal, Lanczos (now) | 84.4 | 15.6 |

- The downscale, not the bitrate, was why the owner saw 1440p as far better: 1080p High and Max add 1 to 2 points, Lanczos adds 7.
- Scaling alone with no encoder, on captures of the same moment (concurrent `gfxcapture` sessions get the same frames, PSNR infinite): the GPU's bilinear as shipped 81.7, CPU bilinear 82.4, area 85.4, bicubic 91.9, Lanczos 93.0.
- `gfxcapture`'s `scale_mode=bicubic` is byte for byte its bilinear in every `resize_mode` on FFmpeg 9.0.2 (`point` does differ), so the sharper scale has to be the CPU one. Recheck on every FFmpeg update: a working bicubic there would give this quality at the old cost.
- Cost of the scaler on a moving 2560 x 1440 window at 1080p60, AMF, two rounds: GPU scale 33 and 37%, whole read with CPU bilinear 62 and 48%, bicubic 62 and 57%, Lanczos 60 and 61%. The filter costs nothing, the readback does. After the change a moving test window held 1080p at 68%, 1440p at 70%, 720p at 73%, and a monitor at 64% of a core.
- AMF options on Normal at 1440p: `-quality speed` and `-high_motion_quality_boost_enable` are identical to balanced on this GPU, `-preanalysis` and `-vbaq` give 95.5, `-quality quality` 95.7 at 24.9 Mbps. Not worth a change. High adds 1.8 for 21% more bits, Max a further 0.1 for 9%.
- Exports of that 1440p Normal clip against the same raw source: the old Unlimited ceiling of 1080p at 11 Mbps 87.2, 1080p at 20 Mbps 90.5, at 30 Mbps 92.1, 1440p at 20 Mbps 92.9, at 30 Mbps 94.2. So the ceiling's claim that more gained nothing was false, and it is gone (see Export).
- The owner's report that a 1440p clip showed 1920 x 1080 was the export row: the clip was 2560 x 1440 on disk, its card said so, and the row described the export. Exports now keep the clip's own size whenever no plan forces a re-encode.
- Discord's limits, checked 8 and 9 November 2026 against third party pages, since Discord's own help pages refuse scripted fetches: Free is 20 MB since August 2026 (Neowin and others), Nitro Basic 50 MB everywhere; Nitro reads 500 MB on most pages and 1 GB on the newest (dated 3 November, citing a Discord update of 22 October). The owner picked 500 MB, which fits either way, and asked for a Free mode beside the two Nitro plans. Recheck when a plan's number changes.
- AMF hung twice in the offline scoring as well, once for 17 minutes on `1080p Max` and once on an export, with the owner's Medal also using the encoder. Scoring tools need the same kill timer the app has.

Tools, all throwaway and never committed:

- Harness: a file-based app on `CaptureController`, `LETSCLIP_ROOT` on a sandbox holding a copy of the real settings, `UpdateSettings` per step, a save, `MediaProbe` on the clip. Started through `explorer.exe` on a `.vbs` that runs the `.cmd` hidden, so no console takes the game's focus and the microphone is real (a process from the Claude app hears silence). The script stops the owner's copy, waits 10 s for AMF, runs, then starts the install again with `--tray`, which shows no window over a game.
- Sampler: `Get-Process` deltas and `\GPU Engine(*)\Utilization Percentage` every 4 s, keyed by pid; on AMD the engines are named `3d`, `copy`, `compute 0`, `video codec engine`. Check the game's FPS by cropping its own counter out of the saved clips.
- Scoring: capture raw NV12 from the chain with `-c:v rawvideo`, refuse a sample where under 45% of pixels move (`tblend`, `blackframe`), encode each option from it at the capture's arguments, `libvmaf` against the raw. `gfxcapture` sends only when pixels change: a still window, a repaint of the same pixels and a 2 pixel blink each stall a `-frames:v` capture. Use a moving window or concurrent captures.

## Launch To Clip Research, 9 November 2026

Every step from Windows starting LetsClip to a saved clip, with default settings, measured on the i5-1335U against the owner's log of that morning (v2.0.1 started at login in the tray, Quick Sync on the GPU route).

| Step | Measured | Done |
|---|---|---|
| Windows and the .NET single file host, before `OnStartup` | 0.44 s warm; 3.4 s on a version's first start (unpacking 432 MB) and 5 to 13.5 s when Defender was scanning a new exe | Nothing in LetsClip's code; old extractions are now deleted (see Where Data Lives) |
| `App.Start` in the tray | 0.6 to 0.7 s: monitors 60 ms, the settings JSON line 110 ms, the window 240 to 320 ms, hotkey and tray 150 ms | The startup registration (0.3 to 0.84 s of COM on its first call) moved to a pool thread; `Ready N ms` logs the total |
| `Show()` when started with the window | 2.45 s on the UI thread, WebView2 and Blazor loading | Left: only a start with the window shown pays it |
| The UI thread free for the first window message | 2.9 s (shown start), 8.3 s at login, 9.0 s on 6 November | Now right after `Ready` (575 to 705 ms in the sandbox) |
| The game library scan | Its continuation waited for the UI thread | Runs and applies inside a step off the UI thread, logged with its time (11 to 25 ms on the i5, which has no game libraries) |
| Waiting for a game | `Process.GetProcesses()` every 2 s, 0.36% of a core | Left: WMI process events would be more code for nothing |
| Game found to Recording | 12.7 s on the first start after an update (9.3 s of it the encoder test), 3.4 s warm | The encoder test warms up at launch (see Capture) |
| The two FFmpeg processes and one second segments | Defender's CPU did not move while segments were written (10.6% idle against 8.5%, noise) | Left |
| A press in Games Only with no game | `Nothing is being recorded right now`, nothing else | The balloon offers the app in front (see Automatic Recording) |
| F10 to file | 1.4 to 2.7 s: 1.1 to 1.6 s waiting for the press's segment, 0.13 to 0.18 s stream copy, 0.1 s probe; the chime plays at the press | Left; the log now splits the wait from the writing |
| The 1.2 s gap before ffprobe in the owner's 2.7 s save | Not reproduced: 16 rounds of a 78 MB copy then `Process.Start(ffprobe)` took 7 to 63 ms, in Temp and in Videos | A start of 100 ms or more now logs its time |

Researched and left alone, each with its reason: taking the press time from `GetMessageTime` (the hotkey is handled within milliseconds, the clip end was measured within 20 ms), awaiting a segment event instead of 100 ms polls (saves 50 ms on average), joining the press's segment while it is still open (saves about a second, but the chime already answers at once and reading a growing file risks a cut packet), `-segment_wrap` to reuse segment files (FFmpeg would overwrite files a slow save still reads), dropping `+faststart` (a shared clip then has to download whole before it plays), skipping the read back probe (0.1 s for knowing the file is whole), and an in-memory ring (more code, no gain).

A second pass the same day measured what recording costs while it runs, on the i5 with Quick Sync on the GPU route, one core's share per process, a 30 s clip each:

| Setup | Video FFmpeg | Audio FFmpeg | LetsClip | Video FFmpeg memory | Frames |
|---|---|---|---|---|---|
| A 1600 x 900 test window as the only added app, the new defaults | 12.9% | 2.2% | 1.2% | 201 MB | every frame it drew (about 40 FPS) |
| The same, microphone off | 13.0% | 1.1% | 1.0% | 195 MB | the same |
| Monitor 1080p60, the new defaults | 28.9% | 2.8% | 1.7% | 252 MB | 1806 |
| Monitor 1080p60, microphone off | 25.2% | 1.9% | 1.7% | 244 MB | 1781 |

- The microphone with Noise Cancellation, now on by default, costs about 1% of a core.
- The window: the page starts 1.3 to 1.6 s after the process (Debug) and paints 0.56 to 0.73 s later; its 8 files (1.2 MB, MudBlazor's CSS the largest at 610 KB) load in 15 to 25 ms each, so nothing there is worth trimming. A tray start never opened holds LetsClip at 94 MB with no WebView2, at 0.76% of a core while waiting for a game. Once opened, LetsClip holds about 165 MB and WebView2's six processes 222 MB; hiding suspends the page and only brings WebView2 to 175 MB, so about 340 MB stayed for the rest of the session. Now ten minutes in the tray release the page (`MainWindow.ReleasePageAsync`, the owner's pick): the BlazorWebView is disposed and WebView2's processes exit, measured from 6 processes and 206 MB to none, and LetsClip from 173 to 145 MB; the next open builds a new page (`EnsurePage`, about 1.5 s like a tray start's first open), on the editor when a balloon asked for a clip. Every component is disposed within milliseconds (checked with the editor open too, its pending trim written first), but the host's own disposal then waits on the hidden window, so after 5 s WebView2 is disposed directly. A running export keeps the page (`ClipExporter.IsExporting`), a show or hide meanwhile starts the wait again, the page is woken first, and the window leaves full screen. The page is no longer in `MainWindow.xaml`: it is built on the first show, so a tray start creates none until opened.
- The game check lists 374 processes in 3.8 ms of CPU and 557 KB of garbage every 2 s, 0.19% of a core. A check from visible windows would be cheaper but opens a handle on each game process, which anti cheat protected games can refuse, so it stays.
- The bundle: FFmpeg is 181 MB of the 432 MB unpacked (`avcodec` alone 114 MB), the rest .NET, WPF and the app. Only a custom FFmpeg build with just the codecs LetsClip uses would shrink downloads, updates and first starts; not started.
- A RAM ring instead of disk segments (as Medal says it does) would spare about 9 GB of writes per hour of gaming at 20 Mbps, which a consumer SSD rated for 600 TB takes for decades, so the disk stays.
- FFmpeg against other engines (searched on 9 November 2026): every recorder uses the same NVENC, AMF and Quick Sync hardware, so the encoding itself is equal; what differs is keeping frames on the GPU. Media Foundation is not consistent across vendors (a 2026 benchmark found NVIDIA's current driver registers no H.264 encode transform), OBS's libobs brings a whole compositing engine, and the vendor SDKs mean three native integrations with their own driver quirks, which is what FFmpeg absorbs. The one gap left is the frame readback on AMD and NVIDIA (see Capture); `scale_d3d11`'s bind flag question was still open on the FFmpeg list in June 2025. NVENC can take `ddagrab`'s D3D11 frames directly when no scaling is needed, untested for want of an NVIDIA card.

Tools, all throwaway and never committed: a Release publish with temporary `TIMING` lines in `App.Start` run three times in a `--tray` sandbox and twice shown; a file-based app timing `WScript.Shell`'s shortcut save to a scratch path; one repeating `ClipSaver`'s copy and probe on 33 noisy x264 segments; Defender's `% Processor Time` counter while a test FFmpeg wrote segments; a harness on `CaptureController` with Notepad as the only added app, then monitor mode, in a sandbox; and a Release publish with the sandbox check of `DeleteOldUnpackedVersions` lifted, run beside the owner's running copy, which deleted v1.1.1 to v1.7 and two idle test extractions and kept the running copy's. The second pass added a WinForms test window drawing moving shapes (keyed `pattern`), a harness recording it and the monitor through `CaptureController` with settings passed as a file (PowerShell 5.1 strips the quotes from a JSON argument to a native program), a script running the capture's own FFmpeg chain with one option changed per run and sampling its private memory, CPU and progress frames over 15 s, a harness on the app's own `CaptureSession` with x264, a `Process.GetProcesses` timer, and CDP's `performance` entries for the page. CPU readings of the live desktop swung from 110 to 205% between identical x264 runs, so only memory and frame counts were compared there.

## Handoff Round, 10 November 2026

A whole app review written on the owner's laptop asked for nine rounds; its phase 1 numbers were repeated on the gaming PC first and every finding held (the Clips page is about three times faster here than on the i5 and has the same shape). Done in one round: Clips in batches (`Docs/Layout.md`), no WPF window in a tray start, the page release collection and the baselines (`Docs/Verify.md`), the crash log, the Windows restart, the recovery gaps and guarded timers (`Docs/Recovery.md`, `Docs/Capture.md`), Steam's folder from the registry with the Xbox and Rockstar fixes and the failure chime (`Docs/Capture.md`), the install folder and update safety (`Docs/Release.md`), `MainWindow` split into the page host and `Tray`, this file split out of `CLAUDE.md` (172 KB to 23 KB) and one FFmpeg update checklist (`Docs/FFmpeg.md`).

- Not done: .NET 11 and `UseCompositionControl = false`. Only SDK 10.0.401 is installed; installing an SDK is the owner's call. The steps are in the handoff's Round 5.
- `scale_d3d11` on the RX 9070 XT: every NV12 texture array of 10 fails with `0x80070057` except for the decoder, every single NV12 texture is created with the filter's own flags, the same as on Iris Xe (P010 with the encoder flag fails even singly). So FFmpeg's one line (`initial_pool_size = 0`) would apply to AMD. Recorded only, the owner wants no patched FFmpeg build unless asked.
- Ponytail pass over the round: nothing found to cut beyond what the split itself removed (`MainWindow.Attach`, its tray fields and the window handle borrowed for the menu). Left on purpose: `s_steamRoots` as the fallback, `IsStartFailure` for the encoder loop.
- Left for later, noted in the handoff and not touched: orphaned metadata of clips deleted while LetsClip was off, an `alimiter` on the audio mix, signed updates, a stable exe name, a card component for the Clips page.

## .NET 11, Signing And The Sweep, 11 November 2026

The owner's answers to the handoff's open questions: install .NET 11 and do round 5, sign updates, the `01 Apps` folder does not matter (it stays where it exists). Done the same night, with a Ponytail ultra audit and a consistency sweep, then released as v2.0.4.

- Ponytail ultra audit, whole tree (128 files, 18,008 lines, every member, CSS class and custom property named only where declared): nothing dead beyond the P/Invoke struct fields, `field-warning` and `field-error` (built by `FieldDetail`) and the extension class. Cut: `Clips.CallPageAsync` (a wrapper that only delegated, now a `Reference` property and two direct calls), four hand written `JSDisconnectedException` blocks (one `PageScript.CallAsync`), `ExportPanel.Request`, `MainWindow.Attach` and its tray half (`Tray`). Net about 60 lines gone, no dependency to drop.
- Left alone on purpose: orphaned metadata of clips deleted while LetsClip was off (needs the path in the metadata), an `alimiter` on the mix (a media change that wants its measured test), a card component for the Clips page, a stable exe name.
- Numbers: `Docs/Release.md` (.NET 11 host, signing), `Docs/Layout.md` (sweep), `Docs/Verify.md` (baselines).

## Performance Report, 12 November 2026

Measured on the work laptop (i5-1335U, Iris Xe, Quick Sync) in Debug sandboxes over CDP, the owner's pick being report only: nothing below was changed. Ranked by what a person would notice. Numbers on the gaming PC will be smaller.

| # | What | Where | Measured | Possible fix | Effort |
|---|---|---|---|---|---|
| 1 | A fresh library's cards wait for the whole picture pass: the page re-renders when the pass ends (`PicturesDone`), so a picture already on disk shows only then | `ClipLibrary.FillThumbnails`, `Clips` | 60 new clips: cards at 8 ms, the first spinner at 8 ms, up to 46 spinning at once, the last picture at 73.1 s with two captures running beside it | Raise per picture, or once a second while a pass runs | Small |
| 2 | A long selection encoded for Nitro Basic on Quick Sync overshoots and falls through to x264 two pass | `ClipExporter` retries | A 5 minute selection: 51.4 then 50.7 MB for a 50 MB limit (the retry lowered the bitrate 5.6% and the size only 1.4%), then x264 two pass ran past 10 minutes | Scale the retry's step by the overshoot, or cap Quick Sync with `maxrate`; a media change, so measured on FFmpeg's sources first | Medium |
| 3 | A 10 minute clip's timeline strip | `ClipEditor.LoadStripAsync` | 7.15 s to the strip (its spinner covers it), the player and side panel ready long before | Decode the 12 tiles in parallel seeks instead of one pass over every keyframe | Medium |
| 4 | Ten minute replays | `CaptureSession` | 23 to 24% of a core for both FFmpeg processes, LetsClip 1.1%, memory flat over 10 minutes (`Docs/Capture.md`) | None needed | |
| 5 | The game check every 2 s | `GameWatcher` | 0.19% of a core (9 November, unchanged code) | A window based check opens game handles anti cheat may refuse, so it stays | |
| 6 | Check for Updates | `Updater` | 0.12 to 0.52 s online, up to the 10 s timeout offline; More now spins meanwhile | None needed | |
| 7 | Read, not measured: `ExportPanel` asks `File.Exists` for the exported file on every render, `Clips.ShownClips` filters and sorts the library on every render, and a failed session start's `Shutdown` waits on the UI thread | `ExportPanel`, `Clips`, `ReplayRecorder` | Sub-millisecond at 60 clips by reading; nothing showed in the frame recordings | Cache per render if a 1,000 clip library shows it | Small |

Idle and playback, 20 s windows, one core = 100%:

| State | LetsClip | WebView2 (7 processes) |
|---|---|---|
| Clips, top | 0.23% | 0.15% |
| Clips, scrolled (the bar's shadow drawn) | 0.92% | 0.23% |
| Settings | 0.61% | 0.15% |
| Editor, paused | 0.23% | 0.00% |
| Editor, a 2 minute 1080p clip playing, the timeline's mask on | 1.04 to 1.20% | 22.5 to 24.0% |
| The same with the mask swapped for the old overflow clip | 0.90 to 1.35% | 19.9 to 25.9% |

So the header bar's scroll driven shadow and the timeline's mask cost nothing measurable. The editor opened a 2 minute clip with its strip cached in 394 ms. Memory: LetsClip 245 to 251 MB and WebView2 332 to 397 MB across these states in a Debug build.

Tools, all throwaway: a CDP driver (`cdp.js` with `until` and `evaluate`), page state scripts putting the page in each state, a PowerShell sampler of a sandbox's LetsClip and its own WebView2 processes by their command line, and per frame recorders of spinners and disabled controls (`Docs/Verify.md`).

## Handoff Round On The Gaming PC, 12 November 2026

The work laptop's round came in on a `temp` branch with a handoff file and was finished here: squashed onto `master`, checked on AMF and the owner's scale, cleaned up and released as v2.0.7.

- Numbers: `Docs/Capture.md` (5 and 10 minute replays on AMF), `Docs/Export.md` (copy weights, Nitro Basic on AMF), `Docs/Layout.md` (sweep, header bar, style dump, locks).
- Ponytail ultra audit, whole tree: every identifier named once in the sources is a framework name or a P/Invoke field, every style class but `field-warning` and `field-error` (built by `FieldDetail`) is used, every custom property is read, every page script export is called. Cut: `ExportPanel.TooLongText`'s unreachable fallback, `ClipExporter.TooSmallMessage` made private, a comment above the `PacketMap` record, 55 over long comments. Left alone: the four small spinners written out where they show (a component for three attributes would be a bare wrapper), `SizeBudget.KeepsClip` as the exporter's own first guess.
- Performance, report only, the owner's rule: a Discord plan export of 5 minutes takes 90 s on AMF (about 200 FPS, `-quality quality`, `-preanalysis 1`, three B frames, decode and Lanczos on the CPU) and 149 s when x264 takes over. A faster AMF preset or a GPU decode would shorten it and wants a measured quality test first. The picture pass, the Quick Sync overshoot and the 10 minute strip from the table above stay open picks.
- Open picks for the owner: a whole clip with Off can show its copy a few KB under the file on clips not made by LetsClip (605 against 602 KB on the portrait test clip), true but it reads as a change; the timeline keeps its look during a cut or restore.

## Improvement Round, 13 November 2026

- Page release: from 9 to 12 November every release in the tray failed (`InvalidCastException`, 7 of 7 in the owner's logs). With `PageWebView` the sandbox released in 38 ms and all six WebView2 processes exited; LetsClip itself then held 83 MB.
- Game window crash, reproduced (`Docs/Recovery.md`): window capture with AMF crashed after 29.8 minutes of resize stress, in `amdxx64.dll`; window capture with x264 and monitor capture with AMF held two hours each. The separate-device fix then ran beside a shared-device baseline for four hours.
- Plan export of 60 s 1080p60 to Free at 720p: decode 7.0 s, decode and Lanczos 8.6 s, GPU decode and Lanczos 7.3 s, full export 19.1 s with pre-analysis and 11.3 to 14.1 s without, VMAF 68.05 and 68.00.
- Capture CPU at 2560 x 1440 to 1080p60 with AMF, 20 s: CPU Lanczos 9.3 s of CPU, `gfxcapture` scaling 5.2 s.
- Codecs at the same budget, 20 s of Overwatch, VMAF: 2.4 Mbps 720p H.264 72.2, HEVC 76.2, AV1 77.2; 1080p H.264 67.4, HEVC 79.2, AV1 78.9. 4.8 Mbps 720p 85.8, 87.9, 88.4; 1080p 84.5, 91.6, 91.8.
- Voice: `speechnorm` evened a 10 dB quieter speaker to 4 dB; FFmpeg's `arnndn` crashed 4 to 13 times in 120 to 200 runs on raw PCM input and 0 times in 282 runs on generated input; the current chain 0 in 300.
- HEVC on AMF, VMAF against the source, 20 s Overwatch cut (fight in brackets): 0.8 Mbps 1080p 55.8 (54.6), 720p 51.6 (51.8), 540p 47.2 (48.0); 1.2 Mbps 63.9 (64.0), 60.7 (61.0), 55.9 (56.6); 1.8 Mbps 72.1, 69.8, 64.8; 2.4 Mbps 78.6, 75.8, 70.5; 3.6 Mbps 86.7, 83.3, 77.8; 5 Mbps 92.0, 88.3, 82.7. Asked against written at 1080p: 0.8 to 1.03, 1.2 to 1.37, 1.8 to 1.93, 2.4 to 2.56 Mbps.
- Crash soak from 14:47 to 16:12 (every 9 s a minimise, a burst of 40 sizes, a hide, a 1 x 1, windowed or a move): the shared-device baseline crashed twice (73.9 and 5.7 minutes), the two separate-device copies none; the first shared-device run had crashed once in 3.75 hours. Same faulting address every time (`Docs/Recovery.md`).

## Card Menu And Install Round, 13 November 2026

- An Off selection's Bitrate row over its length, 1440p60 noise clip at 28.9 Mbps with a keyframe each second, starting at 10 s: 20 s 30.2 Mbps, 10 s 32, 5 s 34.9, 2 s 43.9, 1 s 58.3, 0.5 s 86.8 (sizes 75.6, 40, 21.8, 11, 7.3 and 5.4 MB). The x264 test clip, 183 kbps whole: 10 s 200, 2 s 275 kbps. The keyframe before Cut Start and the frames up to it are in every copy.
- The main button: Copy File for the whole clip at Off with sound, Export once muted or on a plan the clip is over, Save and Copy for a shorter selection. Save and Copy on the 20 s noise clip from 5 s asked first, cut it to 15 s, showed Saved the cut and Copied, and the clipboard held the clip's own path; no Exports folder was made.
- Live filter: unticking Overwatch 2 read `3 of 5` with the popover still open, Reset read `5` with every box ticked and itself disabled. The editor's chip opened Clips at `2 of 5`, scrolled to the top.
- Reset to Default from the sandbox's settings (Save Sound off, Replay Length changed) wrote the defaults and kept the window and the muted player; the button then read disabled.

## Own FFmpeg, GPU Route And Names Round, 14 November 2026

7800X3D, RX 9070 XT (driver 32.0.32015.7029), the 2560 x 1440 monitor.

- LetsClip's own FFmpeg 9.0.2: 28 MB of files against BtbN's 181 MB (`avcodec` 14.4 MB against 114 MB), the release exe 105 MB against 168 MB, its unpacked folder about 233 MB against 432 MB. All 18 command forms LetsClip runs passed on it.
- The GPU route (`scale_d3d11`, patched): a 20 s FFmpeg capture scaled to 1080p60 took 2.2 s of CPU against 11.7 s on the CPU route (11% of a core against 58%); in the app at the monitor's own size and 60 FPS the video FFmpeg held 15.6% of a core. Colour bars: 65.06 dB between the GPU route and the CPU's BT.709 conversion, limited range on both; against `testsrc2` itself the chroma read 24.8 dB on the GPU route and 26.6 dB on the CPU route, both low from subsampling a test pattern. The GPU downscale from 1440p to 1080p read 43.3 dB on Y against Lanczos, where CPU bilinear reads 44.9, area 46.2 and bicubic 54.1, so scaling stays on Lanczos.
- Games: Windows listed 40 played exes on the owner's PC (`GameConfigStore`), the scan found the same 17 games as his Games folder, two of them (Call of Duty, RV There Yet) now with their real exe instead of a launcher.
- The replay cache's writes: a 1024 MB file written and closed reached the disk as 1001 MB without the temporary attribute and 1100 MB with it, so memory backed files save nothing once FFmpeg closes a segment. A game session writes about 9 GB an hour at 1080p60 Normal; the Samsung 990 EVO Plus 2 TB is rated for 1200 TB, three hours a day is 0.8% of it a year.
- The video FFmpeg's memory, peak private bytes over 20 s: capture into AMF 607 MB (608 with `-async_depth 4`, filter threads 4 or 1), AMF encoding a test pattern without capture 562 MB, `ddagrab` into nothing 159 MB, x264 from `ddagrab` 423 MB. AMF's runtime is the 600 MB.
- The install on the real profile from a v2.0.10 test build: a fresh start from a download folder installed `LetsClip.exe`, wrote the Run value, both shortcuts and the Uninstall entry; a newer build started while it ran took over in place; `--uninstall` removed everything but the clips, their details and the settings (WebView2's folder needed the after exit delete, added then).

## Sweep With The Owner's Settings, 14 November 2026, Evening

7800X3D, RX 9070 XT, Debug sandbox. Details in `Docs/Capture.md` (One Recorder Per Process, Sweep With The Owner's Settings) and `Docs/Export.md` (The Whole Clip Is The File).

- A game started by a voice app, 440 Hz: -65.7 dB recorded twice, -43.0 dB after the fix and -43.1 dB started on its own. A voice app's tree: its child's 660 Hz at -43.1 dB, its grandchild's 880 Hz at -71.3 dB.
- 2560 x 1440 window at 1080p60 Normal, a minute, game and voice chat sound, cleaned microphone: video FFmpeg 67.9% of a core and 629 MB, audio FFmpeg 4.8%, LetsClip 1.6%; 3571 of 3600 frames, 60.013 s, 1.4 s from press to file.
- The Jumpscare clip: 25,749,743 bytes on disk, weighed 25,751,315.
- Not done, the owner's word mid round: the Ponytail audit, and anything beyond the sweep. Open for his pick: Steam's own voice chat in clips of Steam games, the 2 s retry wait after a display change, the one AMF crash on monitor capture seen in his log.

## Edge Cases, Audit And Sweep, 14 November 2026, Night

Details in `Docs/Layout.md` (Edge Cases And A Ponytail Ultra Audit) and `Docs/Recovery.md`.

- Name then Export in one press: before, renamed and nothing exported; after, renamed, exported as `Race Test.mp4` (486 KB muted, as Details said) with the Exported panel up. Copy File in the header after a second name: renamed, Exported panel kept. Leaving mid name: renamed after leaving. Delete right after typing: recycled under its own name, the typed name dropped, no warning in the log.
- An added app that is itself a voice app, 440 Hz: -43.04 dB with its own pid left out of the voice apps; a game started by a voice app after the audit's shrink: -43.05 dB.
- Exit mid export: one unfinished file in Exports before, none after, the app gone 0.2 s after the exit signal.
- Not run: a clips folder move failing outright, a folder at a real drive's root, a game scan throwing, a kept original refusing the Recycle Bin. Each was read and built, the paths are a few lines.

## A Live Match, 15 November 2026

The owner played Overwatch on v2.0.12 with the default settings (Games and Apps Only, 1080p60 Normal, a minute, game and voice chat sound, the cleaned microphone at 80%) while the session watched his real install from outside. 7800X3D with 8 threads, RX 9070 XT (driver 32.0.32015.7029), 32 GB, the game at 2560 x 1440 on the 240 Hz monitor.

- His running copy, sampled every 5 s for 20 minutes of match (222 samples, `Get-Process` deltas and the WMI class `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine`, since `Get-Counter` returned nothing from a background shell): video FFmpeg 0.68 of a core on average and 0.88 at most, 639 MB, the video engine 15% and the 3D engine 2%; audio FFmpeg 0.08 of a core and 55 MB; LetsClip 0.01 of a core and 183 MB. Overwatch beside it: 3.1 cores (4.7 at most) and 74% of the 3D engine. So recording takes about 0.77 of the 8 threads, a tenth of the CPU, a fifth of what the game itself uses. Medal measured 0.13 to 0.20 of a core on the same game (Overwatch Field Test), so LetsClip still costs four to five times its CPU, all of it the readback and conversion (see `Docs/Capture.md`, Where The CPU Route's Time Goes).
- His logs of 13 to 15 November: no error and no fatal; on 14 November the three session ends already noted (`Docs/Capture.md`, Sweep With The Owner's Settings), each recording again within 3.5 s; two headset mode switches (`AudioDeviceDisconnectedException`) reconnected within 25 ms without a restart; one hotkey clash with a sandbox. On 15 November the start at sign in took 5.2 s to Ready (0.4 s warm on 14 November), recording began 1.4 s after the game's window was found, and two Microphone Volume changes restarted the whole recording (fixed, `Docs/Capture.md`, Live Microphone Volume).
- Capture chains measured beside his game, 20 s each, see `Docs/Capture.md`: the CPU route 54 to 67% of a core, the GPU route 14.8%, the hybrid with GPU conversion and CPU Lanczos 79 to 88% (rejected), Lanczos, bicubic and `bicublin` alike within the noise.
- Tools, all throwaway in the session's scratchpad: the sampler above, a bench script running one FFmpeg capture chain for 20 s and reading its CPU after a 5 s warm up, the graph comparison and benchmark scripts for the audio, and the three size sweep over CDP.

## Where A Thumbnail Comes From, 15 November 2026

The owner asked for thumbnails from near the end. Frames taken from copies of four of his clips at 1 s in and at 8, 5, 3, 2 and 1 s before the end (two untouched 60 s Overwatch replays from that day's match, two clips he had cut to a few seconds):

- 1 s in showed the hero select screen on one replay and a different fight from the start of the minute on the other.
- 1 s before the end caught the communication wheel open on one replay: the press comes right after the moment, sometimes with a menu on screen.
- 2 and 3 s before the end showed the fight on both replays; 2 s was picked.
- On the cut clips the whole clip is the moment, and the middle (2.2 to 2.5 s of 4.4 to 4.5 s) showed it, where 1 s before the end showed the aftermath on one.
- So a thumbnail is the later of the middle and 2 s before the end. A whole pass over 13 sandbox clips made every picture again with no failure.

## Performance Sweep, 15 November 2026

The owner asked for a thorough sweep with fixes to suggest. Nothing here is applied: performance work waits for his pick.

Live, read only, his v2.0.12 during that afternoon's match (WMI counters over 60 s):

- Video FFmpeg 0.63 of a core, 639 MB private but 160 MB resident (Windows trimmed it beside Overwatch's 20 GB), the cache written at 0.26 MB/s in a menu with 61 writes a second (a flush per packet for windows). Audio FFmpeg 0.08 of a core and 54 MB. LetsClip itself 0.007 of a core, 215 MB private while hidden with its page released (the aggressive collection after the release already runs, so this is the floor with WPF and Blazor loaded) and 0.73 MB/s into the audio pipe (four channels of 32 bit float at 48 kHz on that version).
- Handles 2552 at 14:41 and 2469 at 15:03: no leak. Overwatch found to recording: 1.4 s.
- Saves of 149 to 283 MB took 1.1 to 2.8 s: 0.4 to 1.3 s waiting for the segments holding the press, the remux 0.47 to 0.74 s, the probe back 69 to 688 ms during the match against 35 to 106 ms at rest. A fresh file under Videos probes in 28 to 34 ms, and under every core busy at normal priority in 110 to 162 ms (71 to 120 ms at above normal), so the slow probes in the match are most likely ffprobe's libraries paged back in beside the game.

Measured offline on copies of his clips and on test sources:

- Thumbnails: the exact frame (now) took 0.36 to 0.75 s of CPU, 0.22 to 0.40 s and 97 MB; the keyframe at or before the spot (`-noaccurate_seek -skip_frame nokey`) 0.03 to 0.11 s of CPU, 0.07 to 0.11 s and 45 MB, on two 60 s 1080p60 clips. With LetsClip's one second keyframes the picture then lands 2 to 3 s before the end.
- The video encoder's memory, the window route on a 1440p test feed: QVBR with its preanalysis (now, onnxruntime loaded) 712 MB private, the same with `-async_depth` 1 or 4, the low latency usage or the motion boost off; VBR peak or CQP without preanalysis 289 MB; the graph without an encoder 225 MB. QVBR does not open without preanalysis. The CPU difference was within the noise, about 0.05 of a core. So the 14 November note that AMF's memory had nothing to trim was wrong.
- Quality at the same size, 20 s of his match re-encoded: QVBR 16.7 Mbps, SSIM 0.961, PSNR 37.2 dB; VBR peak without preanalysis 17.6 Mbps, SSIM 0.964, PSNR 39.5 dB. Both metrics punish adaptive quantisation, so VMAF has to decide; the export test of 13 November found preanalysis moved VMAF by under 0.1.
- Audio, 120 s of five channel audio through the capture graph: now 6.5 to 6.8% of a core; the microphone cleaned in mono 5.7 to 5.9%; AAC's fast coder 4.6%, but 17.8 against 19.8 dB signal to distortion on his game audio at the same size, so not worth it; both 3.8 to 4.5%; the graph without encoding 1.6 to 1.8%.
- Saves: remuxing 61 one second segments of a 270 MB minute took 0.52 s, 0.59 to 0.74 s with faststart. Faststart stays.
- The game watcher's scan while no game runs: 1 ms of CPU and 373 KB per scan of 235 processes every 2 s, 0.05% of a core. During a game it does not scan.

## Rate Control And Saves, 15 November 2026, Evening

The owner picked fixes 1, 2, 3, 5, 6 and 7 of the Performance Sweep and the card drag. What was measured before shipping them:

- Preanalysis features, QVBR at 1080p60 on a test feed, private memory: on 543 MB, TAQ off 542, PAQ off 544, the motion boost mode off 543, CAQ off 543, lookahead 0 542, scene and static detection off 542, all of them off 542, onnxruntime loaded in every case. Only turning preanalysis itself off (VBR peak or CQP) drops it.
- VMAF (the libvmaf of a FFmpeg 9.0.2 essentials build already on the PC, read only) against 20 s of the owner's 1080p60 Overwatch clips as reference, AMF at the capture's own settings:

| Sample | QVBR 42 (was) | VBR peak 20 | VBR peak 28 (now) | VBR peak 36 | CQP 22 |
|---|---|---|---|---|---|
| A fight | 39.6 Mbps, 99.43, 1% low 92.9 | 20.2 Mbps, 98.94, 91.8 | 28.6 Mbps, 99.78, 96.4 | 36.8 Mbps, 99.89, 97.2 | 40.1 Mbps, 99.78, 96.7 |
| The hero select screen | 27.3 Mbps, 99.46, 93.3 | 20.4 Mbps, 99.74, 94.1 | 28.5 Mbps, 99.92, 97.3 | 36.0 Mbps, 99.94, 97.3 | 25.9 Mbps, 99.88, 97.0 |
| A calmer stretch | 21.4 Mbps, 98.73, 93.3 | 20.3 Mbps, 99.66, 96.4 | 28.7 Mbps, 99.84, 97.8 | 36.4 Mbps, 99.87, 98.0 | 21.0 Mbps, 99.56, 96.2 |

- The reference is already compressed, so every score sits near the top; the 1% lows show the gap. VBR peak holds its target on calm content too (no padding below it either), so the disk use matches the owner's QVBR clips of that day (20, 27 and 38 Mbps, 28 on average).
- Thumbnails: on a clip whose video ends 10 s before its sound, the exact seek and the plain keyframe seek both wrote nothing past the last frame (the frame decoded but came before the output's start); `-fps_mode passthrough` wrote it at every spot tried, on that clip and two of the owner's, 67 ms each.
- Mono cleaning against stereo cleaning, the same five channel input: the mix, game and microphone tracks identical sample for sample over 30 s.
- The remux's progress report under the save's `-copyts`: `out_time_us=0` for a correct 30 s file, `-37650000` for an out of range cut, 2312 frames reported for 1835 written; only `progress=end` and the frame count being above zero are trusted.
- Saves in the sandbox, monitor capture: 0.6 and 0.7 s from press to file (segment waits 529 to 608 ms, remux 94 to 110 ms, no probe), against 1.1 to 2.8 s for the owner's three saves that afternoon with the probe.
- The settle delay: Frame Rate 60 to 30 and back 1.2 s apart restarted nothing; 60 to 30 alone restarted 2.0 s later, and back to 60 the same.
- Live, the sandbox recording the first monitor at 1080p60: video FFmpeg 176 MB private, 118 MB in RAM, 1087 handles; audio FFmpeg 33 MB, 142 handles.

## Picks Round, 15 November 2026, Evening

- Window GPU route soak, two hours on the 9070 XT (driver 32.0.32015.7029): three GPU route captures at 2560 x 1440 and one CPU route capture of a stress window cycling every 13.6 s through a minimise and restore, 40 random resizes 25 ms apart, a hide and show, 1 x 1, 1600 x 900 and a move, about 21,000 resizes. All four ran 119.8 to 120.2 minutes, ended with code 0 and wrote 335,105 to 336,205 frames each, no warning, error, dropped or duplicated frame. CPU: 5.3 to 5.5% of a core and 156 MB private on the GPU route, 38.0% and 141 MB on the CPU route.
- Timeline strip by one seek per cell: a 10 minute clip in 0.53 s against 1.15 s decoding every keyframe, and a clip whose video ends early still filled every cell.
- Limiter: a loud mix's peak from +3.0 to -0.4 dBFS, quiet sound within 0.001 dB.
- Steam voice chat with its game: game 440 Hz at -43.03 dB, voice app 660 Hz at -43.04 dB, the unplayed 880 Hz band at -70.6 dB.
- A 1700 s recording (1700 video, 1701 audio segments) saved through the join in 2.3 s, probed 1700 s, 702 live replay segments kept.
- Black check: a black clip 16, a normal clip 253, black at 24 or under.
- Vertical exports from the 110 MB 1440p clip, 30 s: Off 29.3 MB (30.8 shown), Free 18.5 MB, both 810 x 1440 HEVC 60 FPS 30.000 s, crop placement by PSNR 40.5 dB (centred) and 32.0 dB (at 0.8) against 5.8 to 6.4 dB elsewhere, 4.5 to 5.0 s each.
- The in-game toast over a top most full screen window recorded as a game: 217 lit samples in the top band after the save, none before.

## The Handoff Round, 16 November 2026

Gaming PC (7800X3D, RX 9070 XT with AMF, NVMe) unless a line says the laptop (i5-1335U, Quick Sync), whose times are upper bounds. v2.0.14 is a build of `master` from `git archive`; the round's build is what became the next release.

- Packet listing, a 30 minute 1080p60 clip with three AAC tracks, 6.36 GB, 108,000 frames, warm: 2.28 to 2.35 s through the round's probe, 3.54 to 3.85 s through v2.0.14's, the probing process at 80 to 115 MB against 540 to 897 MB private; the plain probe 82 to 123 ms either way. A 32 MB clip: 94 to 109 ms against 70 to 81 ms. Laptop, 6.295 GB and 361,173 packets: plain probe 0.14 s reading 5.2 MB; JSON of every stream 8.3 s, 6,003 MB read, 52 MB of output; CSV of every stream 4.8 to 5.4 s, 7.9 MB; CSV of the video alone 3.0 s, 2.3 MB.
- The editor's numbers, eight clips, whole and five drags each: 1,243 values the same mid drag as at rest, and 1,356 the same as v2.0.14's. The 30 minute clip opened in 2.7 to 2.8 s against 3.9 s; the others in 0.12 to 0.31 s on both.
- The listing hung: v2.0.14 back on Clips after 20.1 s; the round open after 20.7 s, the second open 0.2 s. The plain probe hung: a snackbar and Clips after 20.1 s. A strip hung: its spinner gone at 60.6 s.
- A long save through the harness, 1705 video and 1706 audio one second segments of 640 x 360, 363 MB, the live replay 63 s: v2.0.14 2.2 s, lost after one failed remux and after a rename held for 2.5 s. The round 2.2 s; 4.5 s after one failed remux; a clean failure after two; 5.1 s after the held rename; 167.4 s when the first remux hung until its limit of 2 min 44 s. A short save of 61 segments: 0.2 s, 1.2 s after one failed remux, 3.2 s after a held rename. Laptop: a 6.45 GB remux 277 s with `+faststart`, 186 s without.
- Record for 15 minutes, a 1280 x 720 window on the AMF GPU route with three tracks: 901.98 s and 48,113 frames, tracks of 901.998 s, 222 MB, 7.2 s from Stop to file (0.8 s the press's segments, 0.7 s the remux), 65 live segments kept. Its cache grew about 15 MB a minute.
- Save Cut of 24 minutes from the 6.36 GB clip: 8.6 s from the press to its snackbar, the stream copy 4.3 s, with a recording running. `FFmpeg.CopyLimit` gives that file 14 min 43 s, a 2.1 GB clip 6 min 12 s, a 200 MB one 2 min 24 s.
- Zoomed waveforms, v2.0.14 against the round, 22 windows byte identical: 1700 to 1710 s of the 30 minute clip 666 against 109 ms, 600 to 660 s 314 against 123 ms, its last 5 s 620 against 99 ms, windows of the shorter clips 37 to 123 ms either way. Laptop: 19.4 s against 0.26 to 0.28 s. A zoomed strip of the noisy test clips takes 2.4 to 3.3 s on both, which decides when a zoomed window shows.
- Clip details against coarse timestamps, seven clips with a trim, a crop, a title, another game and another date on two of them: every write time rounded up to an even second and one clip replaced cost v2.0.14 seven probes and every detail, the round one probe (the replaced clip) and nothing else; a clip renamed in Explorer with its time 1.5 s off was adopted (v2.0.14 probed it and lost its game and date); an unzipped copy of the folder needed no probe (v2.0.14 two).
- Black check on seven clips: the same verdicts, 47 to 219 ms against 93 to 293 ms. Laptop: 205 to 297 ms against 287 to 458 ms.
- A background update during Record, in the test copies: v2.0.14 began 21 s after the window hid; the round not in 40 s, then 23 s after Stop and hiding again.
- The session's question: v2.0.14 gone 0.2 s after it with the recording lost; the round gone 0.6 s after it with 16.6 s saved; 0.2 s with nothing recording until stopped.
- An encoder upgrade during Record: v2.0.14 ended the recording at 21.9 s; the round ran to Stop at 50.9 s and moved to AMF two minutes later.
- An export with FFmpeg gone: v2.0.14 `Exporting 0%` for good; the round's snackbar within 0.3 s.
- Hiding the window: two writes of `settings.json` on v2.0.14, one on the round, moved or not.
- A folder move to another drive letter, five files and 398 MB: 217 ms, every hash the same, no probe after it, 25.7 MB allocated against 42.5 MB.
- 1,000 clips, nothing changed for it: `ClipLibrary.ListAsync` 169 to 185 ms (199 to 253 ms on v2.0.14), a first list that probes all of them 18 s; back to Clips 57 to 79 ms (53 to 78), a sort 116 to 121 ms (115 to 121), the filter open in 13 to 20 ms (13 to 22), a tick 62 to 71 ms (63 to 84), a search 110 to 160 ms after its 250 ms wait (100 to 145), the next batch of cards 68 to 80 ms (70 to 81).
- Exports on AMF, each on its first try: Free 18.31 MB in 13.6 s and Nitro Basic 46.17 MB in 13.3 s from a 212 MB minute, Vertical with Off 59.85 MB in 5.9 s, a one track copy 210.95 MB in 0.3 s, Nitro 852.5 MB from six minutes in 78 s; Copy for Discord (Free) from a card 17.8 MB in 7.4 s.
- A late attach: `readyState` read 1 after the seek in 100 of 100 tries; 5 of 5 clips stuck with the attach held back 0.8 s, none after the fix.
- The sweep: 33 screens and sizes clean, twice, with a recording running.

## Scrolling, Encoder Floors And Small Exports, 17 November 2026

Gaming PC, Debug sandbox at 1728 x 908 and 1920 x 1009 on the 240 Hz monitor, 59 clips (four copies of the owner's Overwatch clips among them), and AMF on the RX 9070 XT.

- Scrolling Clips, 14 wheel ticks 400 ms apart: before, with the pointer over the cards, a 100 ms frame in three of five runs and 15 to 42 hover previews started; with it on the scrollbar none of either in four runs. After the preview fix, over the cards: 0 previews, worst frame 33 ms, 10 to 14 doubled frames at 240 Hz per run against 5 to 8 off the cards; with the cards' hover fade switched off 3 to 9, within the spread, so the fade stays.
- Hover preview left 260 to 300 ms after resting: 6 of 48 tries left a black tile before, 0 after.
- Header bar shadow, brightness under the bar with content scrolled: 34.9 flat before (no shadow at all), 25.6 at the edge rising to 34.9 over about 20 px after `mask-clip: no-clip`.
- Shadow falloff under a card on the 32 page: the old panel drop fades out within about 23 px, the new two layer one over about 40 px, both 23 to 24 at the edge.
- AMF HEVC floors, asked 30 to 800 kbps, written bitrate at the floor: a 12 s fight at 48 Mbps 720p60 595, 540p60 382, 540p30 268, 360p60 205, 360p30 144, 360p15 98, 240p60 109, 240p30 78, 240p15 58 kbps; a calmer 20 s Overwatch cut at 22 Mbps about half (720p60 285, 540p60 187, 360p60 108, 240p60 63, 240p30 51, 240p15 43). At 60 FPS the larger size scored higher XPSNR at every bitrate it could land (the fight at about 320 kbps: 360p 20.0, 240p 19.9; the cut at 544 kbps: 720p 24.7, 540p 24.4). `hevc_amf` does not start at 320 x 180, 256 x 144, 240 x 428 or 216 x 384; 384 x 216 and 428 x 240 work, and `h264_amf` starts at 256 x 144.
- AAC at low rates, 300 s of a Discord window's sound: 24 kbps mono wrote 23.3 kbps (24.8 with the container), 32 kbps mono 32.2, 32 stereo 32.1, 48 stereo 47.9.
- Exports, each probed: 10 minutes of 1080p60 for Free 428 x 240 HEVC at 60 FPS, 18.83 MB, 251 kbps, 45 s; for Nitro Basic 960 x 540 at 60 FPS, 47.14 MB, 629 kbps, 44 s; its sound alone for Free a lossless copy of 9.9 MB in 2.5 s; the owner's 1h10m49s Discord window recording's sound for Free 17.0 MB of mono AAC at 32.7 kbps in 42 s.
- A stream copy with each picture's duration capped at the selection's end: the same frame counts as before on a 60 FPS cut (822), a still window cut (732) and an x264 B frame clip (1214); durations 13.2000 against 13.2005 s, 150.500 against 151.021 s and 20.000 against 20.117 s.

## Round 3, 17 November 2026

| What | Before | After |
|---|---|---|
| Window capture, 2560 x 1440 to 1080p60, video FFmpeg | 47.3% of a core, 214 MB, 2301 handles (CPU route, spare device) | 5.5% of a core, 94 MB, 1186 handles (GPU Lanczos) |
| Scaled picture against the CPU's Lanczos, RGB PSNR | 50.3 dB (stock `gfxcapture`) | 61.4 dB (LetsClip's Lanczos) |
| Disk written while recording | 3.17 MB/s, about 11 GB an hour | Nothing until a save, or past 1 GB of replay |
| 7 minute Record saved | 1700 s joined and saved in 2.3 s (16 November) | 425.8 s, 1.18 GB, a gigabyte spilled, saved in 1.8 s |
| HEVC against H.264 on AMF at capture rates, clean 540p reference | 36.21 / 34.48 / 32.76 dB at 7 / 5 / 3.5 Mbps | 36.60 / 34.98 / 33.41 dB, not shipped |
| Export loudness of the owner's match | -24.7 LUFS, true peak -6.5 dBFS | -16.3 LUFS, true peaks -1.6 (Opus) and -2.0 dBFS (AAC) |

## The Work Laptop's Log, 18 November 2026

Gaming PC (7800X3D reporting 8 threads, RX 9070 XT, AMF), master against the round, each through the same tools (`Docs/Verify.md`).

| What | Master | The round |
|---|---|---|
| Two CPUs starved by four Highest threads (400 ms on, 100 off), a minute | 2 to 3 holes, 100 to 212 ms, packets up to 360 ms late, one drop logged | No hole |
| The same with six threads (600 on, 50 off) | 2 holes, 104 ms, one drop logged | No hole |
| The whole process frozen 9 times for 250 ms | 9 holes, 1471 ms | No hole |
| The whole process frozen 9 times for 400 ms | 10 holes, 2761 ms, one drop logged | 1 hole of 56 ms, logged as one incident |
| The default device reconnected 10 times | 11 starts, 10 holes of 10 ms | 1 start, no hole |
| A process loopback recorder switched 10 times | 10 holes, 41 to 48 ms each (478 ms) | No hole |
| A save of a minute, waiting for the press's segments | 810 to 1047 ms, file in 1.0 to 1.2 s | 870 to 1106 ms, file in 1.0 to 1.3 s |
| Second monitor 165 to 144 Hz and back, Record running on the first | Session lost twice, Record saved at the change (1m4.5s), next save 9.5 s | Duplication back in 5 and 2 ms, Record of 87.00 s picture and 87.01 s sound to Stop, next save 60.01 s, one 233 ms held frame |
| Update checks at a shown start | Two, 10 ms apart | One |
| All on, then off, in Games and Apps (30 apps) | 30 in `AddedGames` and 30 in `RemovedGames` | Both empty, the old `claude` leftover too |
| A single click on the editor's Video button | One change | One change (a double click 77 ms apart flips twice on both, as on the laptop) |
| `WEBVIEW2_` variables in the capture's FFmpeg processes | `WEBVIEW2_USER_DATA_FOLDER` and the browser flags | None |
| Recording the 2560 x 1440 monitor at 1080p60 in the tray, 30 s | LetsClip 1.0% of a core and 294 MB, audio FFmpeg 7.7%, video FFmpeg 58.8%, 34 threads | 0.9% and 315 MB, 8.6%, 59.7%, 33 threads |

- Packets reaching a normal priority recorder in that starvation arrived up to 149 to 360 ms late; a recorder in the `Audio` class stayed on time (latest -19 ms at its end).
- A D3D11 process at AboveNormal CPU priority still has Normal GPU priority (`D3DKMTGetProcessSchedulingPriorityClass` on the Claude app's and a WebView2 browser process), so the capture's AboveNormal FFmpeg never gets ahead of other apps on the graphics card.
- FFmpeg's third build (`ffmpeg.zip` of the `ffmpeg` pre-release): 11.4 MB zipped, 28 MB unpacked, built in about 20 minutes at idle priority with four jobs; a 3 s `ddagrab` capture into AMF kept 180 of 180 frames.
