# Fetches And Checks LetsClip's Pinned FFmpeg Build
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$url = 'https://github.com/poolfullofmilk/LetsClip/releases/download/ffmpeg-9.0.2-letsclip-2/ffmpeg-9.0.2-letsclip-2.zip'
$expected = '3B768E62DB4E4081DC1CCA9F021431267A64ADF0E165BC26FD3A86CDDD68A666'
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
    Get-ChildItem (Join-Path $root.FullName 'bin') | Copy-Item -Destination $target -Force
    Copy-Item (Join-Path $root.FullName 'LICENSE.txt') (Join-Path $target 'LICENSE.txt') -Force
    Write-Output "FFmpeg 9.0.2 for LetsClip ready in $target"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
