# Fetches And Checks LetsClip's Pinned FFmpeg Build
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$url = 'https://github.com/poolfullofmilk/LetsClip/releases/download/ffmpeg-9.0.2-letsclip-1/ffmpeg-9.0.2-letsclip-1.zip'
$expected = '533D95F9C15255E36758B06F96C126724F6B885B7762222B8D6A3B51F57D3778'
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
