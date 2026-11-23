# Fetches And Checks LetsClip's Pinned FFmpeg Build
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$url = 'https://github.com/poolfullofmilk/LetsClip/releases/download/ffmpeg/ffmpeg.zip'
$expected = '906CC2CE8602F0D3EE33A3C58FEB982E478C833F564A76E079875931911D010F'
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
