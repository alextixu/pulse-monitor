# 建置並以「系統管理員」身分啟動 Pulse（會跳出 UAC 提示）
# 需要管理員權限才能讀取 CPU 溫度（PawnIO 驅動）與控制主機板 / 顯示卡風扇。
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build "src\Pulse.App\Pulse.App.csproj" -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Get-ChildItem "src\Pulse.App\bin\$Configuration" -Recurse -Filter Pulse.exe | Select-Object -First 1
if (-not $exe) { Write-Error '找不到 Pulse.exe'; exit 1 }

Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -Verb RunAs
Write-Host "已以系統管理員身分啟動：$($exe.FullName)"
