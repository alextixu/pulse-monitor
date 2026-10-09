# 準備要和 Pulse 一起打包的 OpenRGB（放到 third_party\OpenRGB，此資料夾不進 git）。
# 建置 / 發行 Pulse.App 時，若 third_party\OpenRGB\OpenRGB.exe 存在，會自動複製到輸出資料夾的 OpenRGB\。
#
#   .\tools\fetch-openrgb.ps1                         # 從已安裝的 OpenRGB（C:\Program Files\OpenRGB）複製
#   .\tools\fetch-openrgb.ps1 -Source D:\OpenRGB      # 從指定資料夾複製
#   .\tools\fetch-openrgb.ps1 -ZipUrl <官方 zip 網址> # 下載 openrgb.org / GitLab 發佈的 Windows zip
#
# OpenRGB 採 GPL-2.0 授權，以獨立程式的形式隨附（Pulse 只啟動它並透過 TCP 溝通）。
# 散佈時請一併提供 third_party\OpenRGB-NOTICE.md 與 licenses\GPL-2.0.txt（建置時會自動放進 OpenRGB\）。
param(
    [string]$Source = (Join-Path $env:ProgramFiles 'OpenRGB'),
    [string]$ZipUrl
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'third_party\OpenRGB'

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null

if ($ZipUrl) {
    $zip = Join-Path ([IO.Path]::GetTempPath()) "openrgb-$([guid]::NewGuid()).zip"
    $extract = "$zip.d"
    try {
        Write-Host "下載 $ZipUrl"
        Invoke-WebRequest -UseBasicParsing $ZipUrl -OutFile $zip
        Expand-Archive $zip -DestinationPath $extract
        # 官方 zip 通常包一層資料夾：找出 OpenRGB.exe 所在的資料夾。
        $exe = Get-ChildItem $extract -Recurse -Filter OpenRGB.exe | Select-Object -First 1
        if (-not $exe) { throw "zip 內找不到 OpenRGB.exe" }
        Copy-Item (Join-Path $exe.DirectoryName '*') $target -Recurse -Force
        $origin = $ZipUrl
    }
    finally {
        Remove-Item $zip, $extract -Recurse -Force -ErrorAction SilentlyContinue
    }
}
else {
    if (-not (Test-Path (Join-Path $Source 'OpenRGB.exe'))) { throw "$Source 內找不到 OpenRGB.exe；請先安裝 OpenRGB 或改用 -ZipUrl" }
    Copy-Item (Join-Path $Source '*') $target -Recurse -Force
    $origin = $Source
}

# 不要把使用者自己的設定 / 記錄帶進發行版。
Get-ChildItem $target -Recurse -Include 'OpenRGB.json', '*.log' -File -ErrorAction SilentlyContinue | Remove-Item -Force

$exePath = Join-Path $target 'OpenRGB.exe'
$hash = (Get-FileHash $exePath -Algorithm SHA256).Hash
$size = '{0:N1} MB' -f ((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
@"
來源：$origin
複製時間：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
OpenRGB.exe SHA-256：$hash
大小：$size
授權：GPL-2.0（見 NOTICE.md / LICENSE-GPL-2.0.txt），原始碼：https://gitlab.com/CalcProgrammer1/OpenRGB
"@ | Set-Content (Join-Path $target 'SOURCE.txt') -Encoding utf8

Write-Host "OpenRGB 已放到 $target（$size）。重新建置 Pulse.App 後會出現在輸出資料夾的 OpenRGB\。"
