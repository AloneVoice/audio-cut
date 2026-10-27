# Builds dist\AudioCut.exe - a single portable exe with ffmpeg embedded.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tools = Join-Path $root 'tools'
$gz = Join-Path $tools 'ffmpeg.exe.gz'

if (-not (Test-Path $gz)) {
    $dl = Join-Path $tools 'dl'
    New-Item -ItemType Directory -Force $dl | Out-Null
    $zip = Join-Path $dl 'ffmpeg.zip'
    if (-not (Test-Path $zip)) {
        Write-Host 'Downloading ffmpeg (BtbN static LGPL build)...'
        curl.exe -sSL -o $zip https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-8.1.zip
    }
    $x = Join-Path $dl 'x'
    Remove-Item -Recurse -Force $x -ErrorAction SilentlyContinue
    Expand-Archive $zip $x
    $exe = Get-ChildItem $x -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    Write-Host "Compressing $($exe.FullName)..."
    $in = [IO.File]::OpenRead($exe.FullName)
    $out = [IO.File]::Create($gz)
    $z = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::SmallestSize)
    $in.CopyTo($z); $z.Dispose(); $in.Dispose()
    Remove-Item -Recurse -Force $x
}

dotnet publish (Join-Path $root 'src\AudioCut\AudioCut.csproj') -c Release -o (Join-Path $root 'dist')
Get-Item (Join-Path $root 'dist\AudioCut.exe') | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
