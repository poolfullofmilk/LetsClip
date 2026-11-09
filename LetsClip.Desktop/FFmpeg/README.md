# FFmpeg

LetsClip ships one pinned FFmpeg build beside itself and never looks on `PATH` or downloads anything at runtime.

| | |
|---|---|
| Version | FFmpeg 9.0.2, `n9.0.2-45-g1e65c6cd5d` |
| Build | BtbN FFmpeg-Builds, `win64-gpl-shared` |
| Release | [autobuild-2026-11-03-12-50](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-11-03-12-50) |
| Asset | `ffmpeg-n9.0.2-45-g1e65c6cd5d-win64-gpl-shared-9.0.zip` |
| SHA-256 | `0d95666ad79ddbbd920860aa06057db39570f7a1efd5bfa9e7606ad4af278e97` |
| Licence | GPL v3 (`--enable-gpl --enable-version3`, includes libx264), text in `LICENSE.txt` |
| Source | [FFmpeg](https://github.com/FFmpeg/FFmpeg) at `1e65c6cd5d`, build scripts at [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) |

Only `ffmpeg.exe`, `ffprobe.exe`, their DLLs and `LICENSE.txt` are kept; `ffplay.exe` is dropped. The binaries are not in git: run `Get-FFmpeg.ps1` once after cloning, which downloads the asset above, refuses it unless the SHA-256 matches, and unpacks it here. A build without these files fails with a message saying so.

Used features:

- Capture: `gfxcapture` (Windows Graphics Capture, game and app windows), `ddagrab` (Desktop Duplication, monitors) and `setpts` for the media clock
- Converting: `hwdownload`, `scale` with Lanczos and `transpose` on the CPU, or `hwmap` and `vpp_qsv` on Intel's GPU
- Encoding: `h264_amf`, `h264_nvenc`, `h264_qsv` and `libx264`, tried in that order of what the PC has, with `h264_metadata` writing the BT.709 colour description, and `aac`
- Microphone cleaning: `asplit`, `pan`, `highpass`, `afftdn`, `agate` and `amix`, the live test adds `volume` and `aresample`
- Files: the `segment` muxer with `mpegts`, the `concat` protocol, `mp4` with `faststart`, stream copies for saves, cuts and exact exports, `pad` for encoded exports
- Pictures: `scale` for thumbnails, `-skip_frame nokey` with `fps`, `tpad` and `tile` for the timeline strip
- Probing: `ffprobe` JSON output, packet times included when the editor opens a clip
- Encoder tests: the `lavfi` source `testsrc2`
