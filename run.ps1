# 建置並以一般權限啟動 Pulse（CPU 溫度與風扇控制需要系統管理員，請改用 run-admin.ps1）
#
#   .\run.ps1              # 代理程式 + 面板發行到 artifacts\dev，啟動 Pulse.exe（並打開面板）
#   .\run.ps1 -Minimized   # 只顯示托盤圖示
#   .\run.ps1 -Demo        # 只開面板，使用示範資料（不需要代理程式、不碰硬體）
#
# 已經有 Pulse 在執行時（例如排程工作啟動的那份），新的 Pulse.exe 只會請它打開面板然後結束。
param(
    [switch]$Demo,
    [switch]$Minimized,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Demo) {
    dotnet build "src\Pulse.App\Pulse.App.csproj" -c $Configuration -nologo -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $ui = Get-ChildItem "src\Pulse.App\bin\$Configuration" -Recurse -Filter Pulse.UI.exe | Select-Object -First 1
    if (-not $ui) { Write-Error '找不到 Pulse.UI.exe'; exit 1 }
    Start-Process -FilePath $ui.FullName -ArgumentList '--demo' -WorkingDirectory $ui.DirectoryName
    Write-Host "已啟動示範模式：$($ui.FullName) --demo"
    exit 0
}

# The agent starts Pulse.UI.exe from its own folder, so both are published side by side.
$out = Join-Path $PSScriptRoot 'artifacts\dev'
foreach ($project in 'src\Pulse.Agent\Pulse.Agent.csproj', 'src\Pulse.App\Pulse.App.csproj') {
    dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $out -nologo -m:1 -nodeReuse:false -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$arguments = @()
if ($Minimized) { $arguments += '--minimized' }
Start-Process -FilePath (Join-Path $out 'Pulse.exe') -ArgumentList $arguments -WorkingDirectory $out
Write-Host "已啟動：$out\Pulse.exe $($arguments -join ' ')"
