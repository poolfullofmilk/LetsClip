# Fetches the pinned FFmpeg build into this folder and checks it before use
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-11-03-12-50/ffmpeg-n9.0.2-45-g1e65c6cd5d-win64-gpl-shared-9.0.zip'
$expected = '0D95666AD79DDBBD920860AA06057DB39570F7A1EFD5BFA9E7606AD4AF278E97'
$target = $PSScriptRoot
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("LetsClip-FFmpeg-" + [guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory $work | Out-Null
try {
    $zip = Join-Path $work 'ffmpeg.zip'
    Invoke-WebRequest $url -OutFile $zip
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw "FFmpeg download does not match the pinned hash: $actual"
    }

    Expand-Archive $zip -DestinationPath $work
    $root = Get-ChildItem $work -Directory | Select-Object -First 1
    Get-ChildItem (Join-Path $root.FullName 'bin') | Where-Object Name -ne 'ffplay.exe' | Copy-Item -Destination $target -Force
    Copy-Item (Join-Path $root.FullName 'LICENSE.txt') (Join-Path $target 'LICENSE.txt') -Force
    Write-Output "FFmpeg 9.0.2 ready in $target"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
