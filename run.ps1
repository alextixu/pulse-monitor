# 建置並以一般權限啟動 Pulse（CPU 溫度與風扇控制需要系統管理員，請改用 run-admin.ps1）
param(
    [switch]$Demo,        # 使用示範資料，不碰硬體
    [switch]$Minimized,   # 啟動後只顯示托盤圖示
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build "src\Pulse.App\Pulse.App.csproj" -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Get-ChildItem "src\Pulse.App\bin\$Configuration" -Recurse -Filter Pulse.exe | Select-Object -First 1
if (-not $exe) { Write-Error '找不到 Pulse.exe'; exit 1 }

$args = @()
if ($Demo) { $args += '--demo' }
if ($Minimized) { $args += '--minimized' }

Start-Process -FilePath $exe.FullName -ArgumentList $args -WorkingDirectory $exe.DirectoryName
Write-Host "已啟動：$($exe.FullName) $($args -join ' ')"
