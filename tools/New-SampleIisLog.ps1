#Requires -Version 5.1
<#
.SYNOPSIS
    動作確認・性能測定用のダミー IIS ログを生成する。

.EXAMPLE
    .\New-SampleIisLog.ps1 -OutputDirectory C:\temp\iislogs -Date 20260907 -Servers WEB01,WEB02 -LinesPerFile 200000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Date = ((Get-Date).AddDays(-1).ToString('yyyyMMdd')),
    [string[]]$Servers = @('WEB01', 'WEB02'),
    [int]$LinesPerFile = 5000
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }

$pages = @('/default.aspx', '/order/list.aspx', '/order/detail.aspx', '/report/Summary.aspx', '/admin/users.aspx')
$statics = @('/css/site.css', '/js/app.js', '/img/logo.png', '/api/data.json')
$agents = @(
    'Mozilla/5.0+(Windows+NT+10.0;+Win64;+x64)+Chrome/124.0',
    'Mozilla/5.0+(Windows+NT+10.0;+Win64;+x64)+Edge/124.0',
    'Mozilla/5.0+(compatible;+Googlebot/2.1;++http://www.google.com/bot.html)'
)
$rand = New-Object System.Random(20260908)
$dateIso = ([datetime]::ParseExact($Date, 'yyyyMMdd', [System.Globalization.CultureInfo]::InvariantCulture)).ToString('yyyy-MM-dd')

foreach ($server in $Servers) {
    $path = Join-Path $OutputDirectory ("{0}-{1}.log" -f $Date, $server)
    $sw = New-Object System.IO.StreamWriter($path, $false, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $sw.WriteLine('#Software: Microsoft Internet Information Services 10.0')
        $sw.WriteLine('#Version: 1.0')
        $sw.WriteLine("#Date: $dateIso 00:00:00")
        $sw.WriteLine('#Fields: date time s-ip cs-method cs-uri-stem cs-uri-query s-port cs-username c-ip cs(User-Agent) cs(Referer) sc-status sc-substatus sc-win32-status sc-bytes cs-bytes time-taken')
        for ($i = 0; $i -lt $LinesPerFile; $i++) {
            $sec = $rand.Next(0, 86400)
            $time = ([timespan]::FromSeconds($sec)).ToString('hh\:mm\:ss')

            # 7 割は静的ファイル (実ログに近い比率にして足切りの効きを確認する)
            if ($rand.NextDouble() -lt 0.7) {
                $uri = $statics[$rand.Next(0, $statics.Length)]
                $query = '-'
            }
            else {
                $uri = $pages[$rand.Next(0, $pages.Length)]
                $query = if ($rand.NextDouble() -lt 0.5) { "id=$($rand.Next(1,9999))&tab=main" } else { '-' }
            }

            # 1 割は社内 IP (10.x = 除外対象)
            if ($rand.NextDouble() -lt 0.1) {
                $ip = "10.1.{0}.{1}" -f $rand.Next(0, 255), $rand.Next(1, 254)
            }
            else {
                $ip = "203.0.{0}.{1}" -f $rand.Next(0, 20), $rand.Next(1, 254)
            }

            $ua = $agents[$rand.Next(0, $agents.Length)]
            $user = if ($rand.NextDouble() -lt 0.3) { "CORP\u{0}" -f $rand.Next(1, 60) } else { '-' }
            $status = if ($rand.NextDouble() -lt 0.03) { 500 } elseif ($rand.NextDouble() -lt 0.05) { 404 } else { 200 }
            $taken = $rand.Next(5, 3000)
            $scBytes = $rand.Next(500, 80000)
            $csBytes = $rand.Next(200, 2000)

            $sw.WriteLine("$dateIso $time 192.168.10.5 GET $uri $query 443 $user $ip $ua - $status 0 0 $scBytes $csBytes $taken")
        }
    }
    finally { $sw.Dispose() }
    Write-Host ("生成: {0} ({1:N0} 行)" -f $path, $LinesPerFile)
}
