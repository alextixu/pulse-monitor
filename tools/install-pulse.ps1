# 讓 Pulse 在登入時自動以系統管理員身分啟動（工作排程器，不會每次跳 UAC）。
#
#   .\tools\install-pulse.ps1                  # 註冊並立即啟動（預設 exe：publish\win-x64\Pulse.exe）
#   .\tools\install-pulse.ps1 -Exe D:\Pulse\Pulse.exe
#   .\tools\install-pulse.ps1 -NoStart         # 只註冊
#   .\tools\install-pulse.ps1 -Uninstall       # 移除排程（不會關閉正在執行的 Pulse；請先 Pulse.exe --quit）
#
# 為什麼用工作排程器：
# - 風扇控制與 CPU 溫度需要系統管理員權限；「最高權限」的排程工作在登入時直接以管理員執行，不跳 UAC。
# - 由排程服務啟動的 Pulse 不屬於任何其他應用程式的程序樹或套件（例如從 Microsoft Store 版應用程式的終端機啟動時，
#   Windows 會把它當成該套件的一部分，套件更新時一起被強制關閉，設定檔也會被導向到套件資料夾）。
param(
    [string]$Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\win-x64\Pulse.exe'),
    [string]$TaskName = 'Pulse',
    [switch]$NoStart,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Re-run elevated with the same arguments.
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Exe', "`"$Exe`"", '-TaskName', $TaskName)
    if ($NoStart) { $argList += '-NoStart' }
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    exit
}

if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "已移除排程工作「$TaskName」。"
    } else {
        Write-Host "沒有名為「$TaskName」的排程工作。"
    }
    exit
}

$Exe = (Resolve-Path $Exe).Path
$user = "$env:USERDOMAIN\$env:USERNAME"

$action    = New-ScheduledTaskAction -Execute $Exe -Argument '--minimized' -WorkingDirectory (Split-Path $Exe)
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
# No time limit (the default stops tasks after 72 h), keep running on battery, restart if Pulse crashes,
# never start a second copy (Pulse is single-instance anyway).
$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Pulse：裝置電量、CPU/GPU 監控、風扇與 RGB 控制（登入時以系統管理員身分啟動）' -Force | Out-Null
Write-Host "已註冊排程工作「$TaskName」：登入時以系統管理員身分啟動 $Exe --minimized"

if (-not $NoStart) {
    Start-ScheduledTask -TaskName $TaskName
    Write-Host '已啟動。'
}
