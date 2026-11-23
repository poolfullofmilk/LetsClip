# Builds LetsClip's Speech Recognition From Source And Zips It With Its Models
param(
    [string]$Work = (Join-Path $env:LOCALAPPDATA 'LetsClip-Whisper-Build')
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$msysUrl = 'https://github.com/msys2/msys2-installer/releases/download/2026-09-27/msys2-base-x86_64-20260927.sfx.exe'
$msysHash = 'AD336CCCFDA47758B5E15CDA993FBBA421115CB0B126697DAEF1EE4DFE37209F'
$sourceUrl = 'https://github.com/ggml-org/whisper.cpp/archive/refs/tags/v1.9.5.tar.gz'
$sourceHash = 'FF1A9053FEB509FF9D7729703355541AE9690073A6B1C40EB692C962E0DC1720'
$modelUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small-q5_1.bin'
$modelHash = 'AE85E4A935D7A567BD102FE55AFC16BB595BDB618E11B2FC7591BC08120411BB'
$vadUrl = 'https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin'
$vadHash = '2AA269B785EEB53A82983A20501DDF7C1D9C48E33AB63A41391AC6C9F7FB6987'
# One Name For Every Build, The Release Asset Is Replaced In Place
$name = 'whisper'

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

$source = Join-Path $Work 'whisper.cpp-1.9.5.tar.gz'
Get-Pinned $sourceUrl $source $sourceHash
$model = Join-Path $Work 'ggml-small-q5_1.bin'
Get-Pinned $modelUrl $model $modelHash
$vad = Join-Path $Work 'ggml-silero-v6.2.0.bin'
Get-Pinned $vadUrl $vad $vadHash

# Bash Ignores The Carriage Returns Of This CRLF File
$env:MSYSTEM = 'MINGW64'
$env:CHERE_INVOKING = '1'
$script = @"
set -euo pipefail
pacman-key --init > /dev/null 2>&1 || true
pacman-key --populate msys2 > /dev/null 2>&1 || true
# Mirrors Refuse Bursts, One Download At A Time
sed -Ei 's/^#?ParallelDownloads.*/ParallelDownloads = 1/' /etc/pacman.conf
# Mirrors Refuse Bursts Now And Then, Three Tries
for try in 1 2 3; do pacman -Sy --noconfirm --needed mingw-w64-x86_64-gcc mingw-w64-x86_64-cmake mingw-w64-x86_64-ninja && break; sleep 20; done
cd "`$(cygpath -u '$Work')"
rm -rf whisper.cpp-1.9.5 build '$name'
tar xzf whisper.cpp-1.9.5.tar.gz
# Plain AVX2 For Every Windows 11 PC, One Static Exe, No OpenMP
cmake -S whisper.cpp-1.9.5 -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=OFF -DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON \
  -DGGML_OPENMP=OFF -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=ON -DWHISPER_SDL2=OFF -DWHISPER_CURL=OFF \
  -DCMAKE_EXE_LINKER_FLAGS='-static -static-libgcc -static-libstdc++'
cmake --build build --target whisper-cli -j `$(nproc)
mkdir -p '$name'
cp build/bin/whisper-cli.exe '$name/'
cp ggml-small-q5_1.bin ggml-silero-v6.2.0.bin '$name/'
cp whisper.cpp-1.9.5/LICENSE '$name/LICENSE.txt'
objdump -p '$name/whisper-cli.exe' | grep 'DLL Name'
"@
$scriptPath = Join-Path $Work 'build.sh'
Set-Content -Encoding ascii $scriptPath $script
& (Join-Path $msys 'usr\bin\bash.exe') -o igncr -l $scriptPath
if ($LASTEXITCODE -ne 0) { throw "The whisper build failed with $LASTEXITCODE" }

$zip = Join-Path $Work "$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive (Join-Path $Work $name) $zip
Write-Output "Built $zip, SHA-256 $((Get-FileHash $zip -Algorithm SHA256).Hash)"
