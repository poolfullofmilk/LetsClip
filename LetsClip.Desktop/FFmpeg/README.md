# FFmpeg

LetsClip ships its own slim FFmpeg build beside itself and never looks on `PATH` or downloads anything at runtime.

| | |
|---|---|
| Version | FFmpeg 9.0.2, the release tarball `ffmpeg-9.0.2.tar.xz` (SHA-256 `8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e`) |
| Build | `Build-FFmpeg.ps1` in this folder, MSYS2 MINGW64 with GCC, shared libraries with every dependency linked in |
| Patch | `scale_d3d11.patch`: single textures instead of an array of 10, which AMD and Intel refuse for an encoder, and BT.709 studio range output |
| Release | [ffmpeg-9.0.2-letsclip-1](https://github.com/poolfullofmilk/LetsClip/releases/tag/ffmpeg-9.0.2-letsclip-1), a pre-release the update check never sees |
| Asset | `ffmpeg-9.0.2-letsclip-1.zip` |
| SHA-256 | `533d95f9c15255e36758b06f96c126724f6b885b7762222b8d6a3b51f57d3778` |
| Size | 28 MB unpacked, against 181 MB for the BtbN build it replaces |
| Licence | GPL v2 or later (`--enable-gpl`, includes libx264), text in `LICENSE.txt` |
| Source | FFmpeg 9.0.2 plus `scale_d3d11.patch`, MSYS2 MINGW64 GCC 16.2.0, x264 0.165.r3222.b35605a, dav1d 1.5.4, libvpl 2.17.0, zlib 1.4, AMF headers 1.5.3, NVENC headers 13.0.19.0 |

Only `ffmpeg.exe`, `ffprobe.exe`, their DLLs and `LICENSE.txt` are kept. The binaries are not in git: run `Get-FFmpeg.ps1` once after cloning, which downloads the asset above, refuses it unless the SHA-256 matches, and unpacks it here. A build without these files fails with a message saying so. To rebuild, run `Build-FFmpeg.ps1` (it fetches MSYS2 and the source, both pinned by SHA-256, into `%LocalAppData%\LetsClip-FFmpeg-Build`), then publish the zip it prints as a new pre-release and pin it here and in `Get-FFmpeg.ps1`.

What the build keeps, and nothing else:

- Capture: `gfxcapture` (Windows Graphics Capture, game and app windows), `ddagrab` (Desktop Duplication, monitors), `setpts` for the media clock, the `lavfi` device for the encoder tests' `testsrc2`
- Converting: `hwdownload`, `scale` with Lanczos and `transpose` on the CPU, `scale_d3d11` on AMD and NVIDIA when nothing is scaled, or `hwmap` and `vpp_qsv` on Intel
- Encoding: `h264_amf`, `hevc_amf`, `h264_nvenc`, `hevc_nvenc`, `h264_qsv`, `hevc_qsv` and `libx264`, `aac`, `mjpeg` for pictures, every bitstream filter (`h264_metadata` writes the BT.709 colour description)
- Decoding: H.264, HEVC, AV1 (dav1d), VP8, VP9, MPEG-4, MPEG-2, AAC, MP3, Opus, Vorbis, FLAC, AC-3, E-AC-3, ALAC and PCM, so clips copied in from other recorders still play, cut and export
- Every native filter, among them the microphone's `highpass`, `afftdn`, `sidechaingate`, `speechnorm`, `alimiter`, `pan`, `amix` and `asplit`, and `showwavespic` for the timeline's sound
- Files: `mov` and `mp4`, `mpegts`, `segment`, `image2`, raw `f32le`, `null`, the `file`, `pipe` and `concat` protocols
- Probing: `ffprobe` JSON output, packet times included when the editor opens a clip
