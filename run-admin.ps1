# 建置並以「系統管理員」身分啟動 Pulse（會跳出 UAC 提示）
# 需要管理員權限才能讀取 CPU 溫度（PawnIO 驅動）與控制主機板 / 顯示卡風扇。
# 長期使用請改用 tools\install-pulse.ps1（排程工作，登入時自動以系統管理員啟動，不跳 UAC）。
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'artifacts\dev'
foreach ($project in 'src\Pulse.Agent\Pulse.Agent.csproj', 'src\Pulse.App\Pulse.App.csproj') {
    dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $out -nologo -m:1 -nodeReuse:false -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Start-Process -FilePath (Join-Path $out 'Pulse.exe') -WorkingDirectory $out -Verb RunAs
Write-Host "已以系統管理員身分啟動：$out\Pulse.exe"
