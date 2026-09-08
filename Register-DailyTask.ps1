#Requires -Version 5.1
<#
.SYNOPSIS
    New-IisAccessReport.ps1 を毎日実行するタスクスケジューラのタスクを登録する。

.DESCRIPTION
    既定では毎日 02:00 に前日分の CSV を生成する。
    ログのコピーやネットワーク共有への書き込みが必要な場合は -UserName / -Password で
    実行アカウントを指定する (省略時は SYSTEM で実行)。

.EXAMPLE
    # 管理者権限の PowerShell で実行
    .\Register-DailyTask.ps1 -At 02:00

.EXAMPLE
    # ドメインユーザーで実行する (ネットワーク共有にアクセスする場合など)
    .\Register-DailyTask.ps1 -UserName CORP\svc_iisreport

.EXAMPLE
    # 登録内容を確認 / 手動実行 / 削除
    Get-ScheduledTask -TaskName 'IIS Access Report'
    Start-ScheduledTask -TaskName 'IIS Access Report'
    Unregister-ScheduledTask -TaskName 'IIS Access Report' -Confirm:$false
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'IIS Access Report',
    [string]$At = '02:00',
    [string]$UserName,
    [securestring]$Password,
    [int]$DaysAgo = 1,
    [string]$ConfigPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'New-IisAccessReport.ps1'
if (-not (Test-Path -LiteralPath $script)) { throw "スクリプトが見つかりません: $script" }
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'config.json' }

$psExe = Join-Path $PSHOME 'powershell.exe'
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}" -DaysAgo {2} -Force' -f $script, $ConfigPath, $DaysAgo

$action = New-ScheduledTaskAction -Execute $psExe -Argument $arguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact($At, 'HH:mm', [System.Globalization.CultureInfo]::InvariantCulture))

# ログが巨大でも打ち切られないよう実行時間の上限は 4 時間。前回が動いていれば新規起動はしない。
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([timespan]::FromHours(4)) `
    -MultipleInstances IgnoreNew `
    -RestartCount 2 -RestartInterval ([timespan]::FromMinutes(15))

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    if (-not $Force) { throw "同名のタスクが既に存在します。置き換えるには -Force を指定してください: $TaskName" }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

if ($UserName) {
    if ($Password) {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
            -User $UserName -Password $plain -RunLevel Highest | Out-Null
    }
    else {
        # パスワードを保存しない (ユーザーのログオン時のみ実行)。常時実行が必要なら -Password を指定する。
        $principal = New-ScheduledTaskPrincipal -UserId $UserName -LogonType S4U -RunLevel Highest
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
            -Principal $principal | Out-Null
    }
}
else {
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
        -Principal $principal | Out-Null
}

Write-Host "タスクを登録しました: $TaskName ($At 毎日 / $DaysAgo 日前を集計)" -ForegroundColor Green
Write-Host "手動実行:   Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "実行結果:   Get-ScheduledTaskInfo -TaskName '$TaskName'"
