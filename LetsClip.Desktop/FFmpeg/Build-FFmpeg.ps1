# Builds LetsClip's Slim FFmpeg From Source And Zips It
param(
    [string]$Work = (Join-Path $env:LOCALAPPDATA 'LetsClip-FFmpeg-Build')
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$msysUrl = 'https://github.com/msys2/msys2-installer/releases/download/2026-09-27/msys2-base-x86_64-20260927.sfx.exe'
$msysHash = 'AD336CCCFDA47758B5E15CDA993FBBA421115CB0B126697DAEF1EE4DFE37209F'
$sourceUrl = 'https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz'
$sourceHash = '8C3850283EB25FA026482078A04051E0BE17347B09EF81A0849BEC15A96E002E'
$name = 'ffmpeg-9.0.2-letsclip-1'

function Get-Pinned([string]$url, [string]$path, [string]$hash) {
    if (-not (Test-Path $path)) { Invoke-WebRequest $url -OutFile $path }
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($actual -ne $hash) { throw "$path does not match the pinned hash: $actual" }
}

New-Item -ItemType Directory -Force $Work | Out-Null
$msys = Join-Path $Work 'msys64'
if (-not (Test-Path (Join-Path $msys 'usr\bin\bash.exe'))) {
    $installer = Join-Path $Work 'msys2-base.sfx.exe'
    Get-Pinned $msysUrl $installer $msysHash
    & $installer -y "-o$Work" | Out-Null
}

$source = Join-Path $Work 'ffmpeg-9.0.2.tar.xz'
Get-Pinned $sourceUrl $source $sourceHash
Copy-Item (Join-Path $PSScriptRoot 'scale_d3d11.patch') $Work -Force

# Bash Ignores The Carriage Returns Of This CRLF File
$env:MSYSTEM = 'MINGW64'
$env:CHERE_INVOKING = '1'
$script = @"
set -euo pipefail
pacman-key --init > /dev/null 2>&1 || true
pacman-key --populate msys2 > /dev/null 2>&1 || true
pacman -Sy --noconfirm --needed make diffutils patch nasm mingw-w64-x86_64-gcc mingw-w64-x86_64-pkgconf mingw-w64-x86_64-x264 mingw-w64-x86_64-dav1d mingw-w64-x86_64-zlib mingw-w64-x86_64-libvpl mingw-w64-x86_64-amf-headers mingw-w64-x86_64-ffnvcodec-headers
# x264 Pulls In MSYS2's FFmpeg, Whose Static Libraries Would Link
pacman -Rdd --noconfirm mingw-w64-x86_64-ffmpeg mingw-w64-x86_64-ffms2 > /dev/null 2>&1 || true
cd "`$(cygpath -u '$Work')"
rm -rf ffmpeg-9.0.2 stage
tar xf ffmpeg-9.0.2.tar.xz
cd ffmpeg-9.0.2
sed 's/\r$//' ../scale_d3d11.patch | patch -p1
./configure --prefix=/ --arch=x86_64 --target-os=mingw32 \
  --pkg-config-flags=--static --ld=g++ --extra-ldflags='-static -static-libgcc -static-libstdc++' \
  --enable-gpl --enable-shared --disable-static \
  --disable-debug --disable-doc --disable-ffplay --disable-network --disable-autodetect \
  --enable-w32threads --enable-d3d11va --enable-dxva2 --enable-amf --enable-ffnvcodec --enable-nvenc \
  --enable-libvpl --enable-libx264 --enable-libdav1d --enable-zlib \
  --disable-encoders --enable-encoder=libx264,h264_amf,hevc_amf,h264_nvenc,hevc_nvenc,h264_qsv,hevc_qsv,aac,mjpeg,wrapped_avframe,rawvideo,pcm_f32le,pcm_s16le \
  --disable-decoders --enable-decoder=h264,hevc,libdav1d,vp8,vp9,mpeg4,mpeg2video,mjpeg,aac,aac_latm,mp3,mp3float,opus,vorbis,flac,ac3,eac3,alac,pcm_f32le,pcm_f64le,pcm_s16le,pcm_s24le,pcm_s32le,pcm_u8,rawvideo,wrapped_avframe \
  --disable-muxers --enable-muxer=mp4,mpegts,segment,null,image2,pcm_f32le \
  --disable-demuxers --enable-demuxer=mov,mpegts,concat,pcm_f32le \
  --disable-protocols --enable-protocol=file,pipe,concat \
  --disable-devices --enable-indev=lavfi \
  --disable-hwaccels
make -j`$(nproc)
make install DESTDIR=../stage
cd ..
mkdir -p '$name/bin'
cp stage/bin/*.dll stage/bin/ffmpeg.exe stage/bin/ffprobe.exe '$name/bin/'
cp ffmpeg-9.0.2/COPYING.GPLv2 '$name/LICENSE.txt'
for file in '$name'/bin/*; do objdump -p "`$file" | grep 'DLL Name' | sed "s|^|`$(basename "`$file"): |"; done
"@
$scriptPath = Join-Path $Work 'build.sh'
Set-Content -Encoding ascii $scriptPath $script
& (Join-Path $msys 'usr\bin\bash.exe') -o igncr -l $scriptPath
if ($LASTEXITCODE -ne 0) { throw "The FFmpeg build failed with $LASTEXITCODE" }

$zip = Join-Path $Work "$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive (Join-Path $Work $name) $zip
Write-Output "Built $zip, SHA-256 $((Get-FileHash $zip -Algorithm SHA256).Hash)"
