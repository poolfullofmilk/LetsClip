# FFmpeg

LetsClip ships its own slim FFmpeg build beside itself and never looks on `PATH` or downloads anything at runtime.

| | |
|---|---|
| Version | FFmpeg 9.0.2, the release tarball `ffmpeg-9.0.2.tar.xz` (SHA-256 `8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e`) |
| Build | `Build-FFmpeg.ps1` in this folder, MSYS2 MINGW64 with GCC, shared libraries with every dependency linked in |
| Patches | `scale_d3d11.patch`: single textures instead of an array of 10, which AMD and Intel refuse for an encoder, and BT.709 studio range output; `gfxcapture_lanczos.patch`: a `lanczos` scale mode for window capture, a Lanczos-2 shader whose reach grows with the shrink; `ddagrab_reopen.patch`: a monitor capture whose Desktop Duplication is lost to a display change (a dock, another monitor switched off or on, a secure desktop) opens it again and repeats its last frame meanwhile, instead of ending |
| Release | [FFmpeg](https://github.com/poolfullofmilk/LetsClip/releases/tag/ffmpeg), the one pre-release tagged `ffmpeg`, which the update check never sees and every rebuild replaces in place |
| Asset | `ffmpeg.zip` |
| SHA-256 | `30775f9371e2d5e7ef612e1e60c7f5459d96c8e3d73cf0152ead76bb49f29b32` |
| Size | 28 MB unpacked, 11.4 MB zipped, against 181 MB for the BtbN build it replaced |
| Licence | GPL v2 or later (`--enable-gpl`, includes libx264), text in `LICENSE.txt` |
| Source | FFmpeg 9.0.2 plus the three patches, MSYS2 MINGW64 GCC 16.2.0, x264 0.165.r3222.b35605a, dav1d 1.5.5, libvpl 2.17.0, opus 1.6.1, zlib 1.4, AMF headers 1.5.3, NVENC headers 13.0.19.0 |

Only `ffmpeg.exe`, `ffprobe.exe`, their DLLs and `LICENSE.txt` are kept. The binaries are not in git: run `Get-FFmpeg.ps1` once after cloning, which downloads the asset above, refuses it unless the SHA-256 matches, and unpacks it here. A build without these files fails with a message saying so. To rebuild, run `Build-FFmpeg.ps1` (it fetches MSYS2 and the source, both pinned by SHA-256, into `%LocalAppData%\LetsClip-FFmpeg-Build`), then replace the release's `ffmpeg.zip` with the one it prints (`gh release upload ffmpeg ffmpeg.zip --clobber`) and pin its SHA-256 here and in `Get-FFmpeg.ps1` (`Docs/FFmpeg.md`).

What the build keeps, and nothing else:

- Capture: `gfxcapture` (Windows Graphics Capture, game and app windows, sized with its patched Lanczos), `ddagrab` (Desktop Duplication, monitors), `setpts` for the media clock, the `lavfi` device for the encoder tests' `testsrc2`
- Converting: `hwdownload`, `scale` with Lanczos and `transpose` on the CPU, `scale_d3d11` on AMD and NVIDIA when nothing is scaled, or `hwmap` and `vpp_qsv` on Intel
- Encoding: `h264_amf`, `hevc_amf`, `h264_nvenc`, `hevc_nvenc`, `h264_qsv`, `hevc_qsv` and `libx264`, `aac`, `libopus` for sound only exports, `mjpeg` for pictures, every bitstream filter (`h264_metadata` writes the BT.709 colour description)
- Decoding: H.264, HEVC, AV1 (dav1d), VP8, VP9, MPEG-4, MPEG-2, AAC, MP3, Opus, Vorbis, FLAC, AC-3, E-AC-3, ALAC and PCM, so clips copied in from other recorders still play, cut and export
- Every native filter, among them the microphone's `highpass`, `afftdn`, `sidechaingate`, `speechnorm`, `alimiter`, `pan`, `amix` and `asplit`, and `showwavespic` for the timeline's sound
- Files: `mov` and `mp4`, `mpegts`, `segment`, `ogg`, `image2`, raw `f32le`, `null`, the `file`, `pipe` and `concat` protocols
- Probing: `ffprobe` JSON output, packet times included when the editor opens a clip
