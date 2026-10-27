# 建置並發佈 Pulse（常駐代理 Pulse.exe ＋ 隨需啟動的介面 Pulse.UI.exe）到同一個資料夾。
#
#   .\tools\publish.ps1                 # 發佈到 publish\win-x64（不影響正在執行的 Pulse；檔案被占用時會失敗）
#   .\tools\publish.ps1 -Deploy         # 先用 --quit 正常關閉 Pulse（風扇交回），發佈，再由排程工作「Pulse」啟動
#   .\tools\publish.ps1 -Output D:\Pulse
#
# 兩個程式共用同一組 DLL；OpenRGB 會被複製到 <Output>\OpenRGB（需先執行 tools\fetch-openrgb.ps1）。
param(
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\win-x64'),
    [string]$Configuration = 'Release',
    [switch]$Deploy,
    [string]$TaskName = 'Pulse'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$agentExe = Join-Path $Output 'Pulse.exe'

if ($Deploy -and (Test-Path $agentExe)) {
    Write-Host '正在關閉 Pulse（--quit，風扇會先交回 BIOS / 驅動）…'
    & $agentExe --quit
    if ($LASTEXITCODE -ne 0) { throw "Pulse --quit 失敗（結束代碼 $LASTEXITCODE），未發佈。" }
}

# The UI exits on its own when the agent quits; never publish over a running copy (its DLLs would be locked).
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Process -Name 'Pulse.UI' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
$ui = Get-Process -Name 'Pulse.UI' -ErrorAction SilentlyContinue
if ($ui) {
    # The UI holds no hardware (fans stay with the agent), so ending it is safe.
    Write-Warning "Pulse.UI 仍在執行（pid $($ui.Id -join ', ')），結束它以便更新檔案。"
    $ui | Stop-Process
}
if (-not $Deploy -and (Get-Process -Name 'Pulse' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $agentExe })) {
    throw "Pulse 正在從 $Output 執行；請加上 -Deploy（會先 --quit）或發佈到別的資料夾。"
}

dotnet build-server shutdown | Out-Null
foreach ($project in 'src\Pulse.Agent\Pulse.Agent.csproj', 'src\Pulse.App\Pulse.App.csproj') {
    Write-Host "發佈 $project …"
    dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $Output -m:1 -nodeReuse:false -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "發佈 $project 失敗。" }
}
Write-Host "已發佈到 $Output"

if ($Deploy) {
    schtasks /run /tn $TaskName | Out-Null
    Write-Host "已由排程工作「$TaskName」啟動 Pulse。"
}
