#Requires -Version 5.1
<#
.SYNOPSIS
    IIS の W3C ログから、BI ツール向けの日次アクセス集計 CSV を生成する。

.DESCRIPTION
    "yyyymmdd-<サーバ名>.log" という命名のログを読み、サーバ × 日 × ページ URL 単位で
    アクセス回数とアクセス人数を集計して CSV に出力する。

    集計処理そのものは src\IisLogAggregator.cs (C#) が行う。数百万行〜数千万行を
    想定しているため、PowerShell のループでは行単位の処理を一切行わない。

.PARAMETER Date
    集計対象日 (yyyyMMdd)。省略時は -DaysAgo で決まる (既定は前日)。

.PARAMETER From / .PARAMETER To
    期間をまとめて集計したいときに使う (yyyyMMdd)。日付ごとに CSV を 1 本ずつ出力する。

.PARAMETER DaysAgo
    Date / From / To のいずれも指定しない場合に「何日前を対象にするか」。既定 1 (前日)。

.PARAMETER Force
    既に同名の CSV があっても上書きする。

.EXAMPLE
    # 前日分 (タスクスケジューラからの通常運用)
    .\New-IisAccessReport.ps1

.EXAMPLE
    # 特定日を再作成
    .\New-IisAccessReport.ps1 -Date 20260901 -Force

.EXAMPLE
    # 過去 1 か月をまとめて初期ロード
    .\New-IisAccessReport.ps1 -From 20260801 -To 20260831
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$Date,
    [string]$From,
    [string]$To,
    [int]$DaysAgo = 1,
    [string]$LogDirectory,
    [string]$OutputDirectory,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$script:LogWriter = $null
$script:HadError = $false

# ---------------------------------------------------------------- ログ出力

function Write-Log {
    param([string]$Message, [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Level, $Message
    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'WARN' { Write-Host $line -ForegroundColor Yellow }
        default { Write-Host $line }
    }
    if ($script:LogWriter) { $script:LogWriter.WriteLine($line); $script:LogWriter.Flush() }
}

# ---------------------------------------------------------------- 設定

function Get-ConfigValue {
    param($Config, [string]$Name, $Default)
    if ($null -ne $Config -and ($Config.PSObject.Properties.Name -contains $Name)) {
        $v = $Config.$Name
        if ($null -ne $v) { return $v }
    }
    return $Default
}

function Read-ConfigFile {
    param([string]$Path)
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    try { return ($raw | ConvertFrom-Json) }
    catch {
        # JSON ではバックスラッシュがエスケープ文字なので、Windows のパスを
        # "C:\inetpub\logs" と 1 つで書くと「認識できないエスケープシーケンス」で失敗する。
        # よくある間違いなので、無効なエスケープだけを二重化して読み直す。
        # (すでに正しく "\\" と書かれている箇所には手を触れない)
        $fixed = [regex]::Replace($raw, '(?<!\\)((?:\\\\)*)\\(?!["\\/bfnrtu])', '$1\\')
        $config = $null
        try { $config = $fixed | ConvertFrom-Json }
        catch {
            throw ("設定ファイルの JSON を解釈できませんでした: {0}`n  {1}" -f $Path, $_.Exception.Message)
        }
        Write-Log ("設定ファイルのバックスラッシュがエスケープされていないため、自動補正して読み込みました: {0}" -f $Path) 'WARN'
        Write-Log '  恒久対応: パスは "C:\\inetpub\\logs" のように \ を 2 つ重ねるか、"C:/inetpub/logs" と書いてください。' 'WARN'
        return $config
    }
}

function ConvertTo-StringArray {
    param($Value)
    if ($null -eq $Value) { return @() }
    return @($Value | ForEach-Object { [string]$_ })
}

# ---------------------------------------------------------------- CSV 出力

function Get-CsvField {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return '' }
    if ($Value.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0) {
        return '"' + $Value.Replace('"', '""') + '"'
    }
    return $Value
}

function New-CsvWriter {
    param([string]$Path, [string]$Encoding)
    switch ($Encoding.ToLowerInvariant()) {
        'utf8nobom' { $enc = New-Object System.Text.UTF8Encoding($false) }
        'shift_jis' { $enc = [System.Text.Encoding]::GetEncoding(932) }
        'sjis' { $enc = [System.Text.Encoding]::GetEncoding(932) }
        default { $enc = New-Object System.Text.UTF8Encoding($true) }  # utf8 (BOM 付き)
    }
    return New-Object System.IO.StreamWriter($Path, $false, $enc)
}

# ---------------------------------------------------------------- 集計エンジンの読み込み

function Initialize-Aggregator {
    param([string]$Root)
    if ('IisLogReport.IisLogAggregator' -as [type]) { return }

    $src = Join-Path $Root 'src\IisLogAggregator.cs'
    if (-not (Test-Path -LiteralPath $src)) { throw "集計エンジンが見つかりません: $src" }
    $binDir = Join-Path $Root 'bin'
    $dll = Join-Path $binDir 'IisLogAggregator.dll'

    # 毎回コンパイルすると 1〜2 秒かかるので、ソースが変わっていなければ DLL を再利用する。
    $needCompile = $true
    if (Test-Path -LiteralPath $dll) {
        if ((Get-Item -LiteralPath $dll).LastWriteTimeUtc -ge (Get-Item -LiteralPath $src).LastWriteTimeUtc) {
            $needCompile = $false
        }
    }
    if ($needCompile) {
        if (-not (Test-Path -LiteralPath $binDir)) { New-Item -ItemType Directory -Path $binDir -Force | Out-Null }
        try {
            Add-Type -Path $src -OutputAssembly $dll -ReferencedAssemblies 'System.dll', 'System.Core.dll'
        }
        catch {
            # DLL を作れない環境 (書き込み不可など) ではソースから直接読み込む
            Add-Type -Path $src -ReferencedAssemblies 'System.dll', 'System.Core.dll'
            return
        }
    }
    Add-Type -Path $dll
}

# ================================================================ 本体

try {
    $root = $PSScriptRoot
    if (-not $ConfigPath) { $ConfigPath = Join-Path $root 'config.json' }
    if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "設定ファイルが見つかりません: $ConfigPath" }
    $config = Read-ConfigFile -Path $ConfigPath

    $logDir = if ($LogDirectory) { $LogDirectory } else { [string](Get-ConfigValue $config 'LogDirectory' '') }
    $outDir = if ($OutputDirectory) { $OutputDirectory } else { [string](Get-ConfigValue $config 'OutputDirectory' '') }
    if (-not $logDir) { throw 'LogDirectory が設定されていません。' }
    if (-not $outDir) { throw 'OutputDirectory が設定されていません。' }
    if (-not (Test-Path -LiteralPath $logDir)) { throw "ログフォルダが存在しません: $logDir" }
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $runLogDir = Join-Path $outDir 'logs'
    if (-not (Test-Path -LiteralPath $runLogDir)) { New-Item -ItemType Directory -Path $runLogDir -Force | Out-Null }
    $runLogPath = Join-Path $runLogDir ('run_{0}.log' -f (Get-Date).ToString('yyyyMM'))
    $script:LogWriter = New-Object System.IO.StreamWriter($runLogPath, $true, (New-Object System.Text.UTF8Encoding($true)))

    Write-Log "==== 集計開始 (config: $ConfigPath) ===="

    # ---- 対象日の決定 -------------------------------------------------
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    function ConvertTo-Date([string]$s, [string]$label) {
        $d = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($s, 'yyyyMMdd', $culture, [System.Globalization.DateTimeStyles]::None, [ref]$d)) {
            throw "$label は yyyyMMdd 形式で指定してください: $s"
        }
        return $d
    }

    $targetDates = New-Object System.Collections.Generic.List[datetime]
    if ($Date) {
        $targetDates.Add((ConvertTo-Date $Date '-Date'))
    }
    elseif ($From -or $To) {
        if (-not $From -or -not $To) { throw '-From と -To は両方指定してください。' }
        $d1 = ConvertTo-Date $From '-From'
        $d2 = ConvertTo-Date $To '-To'
        if ($d2 -lt $d1) { throw '-To が -From より前の日付です。' }
        for ($d = $d1; $d -le $d2; $d = $d.AddDays(1)) { $targetDates.Add($d) }
    }
    else {
        $targetDates.Add([datetime]::Today.AddDays(-$DaysAgo))
    }
    Write-Log ("対象日: {0} 件 ({1} 〜 {2})" -f $targetDates.Count,
        $targetDates[0].ToString('yyyy-MM-dd'), $targetDates[$targetDates.Count - 1].ToString('yyyy-MM-dd'))

    # ---- タイムゾーン --------------------------------------------------
    # IIS のログは既定で UTC。CSV の「日」は運用上のローカル日付にしたいので補正する。
    $logIsUtc = [bool](Get-ConfigValue $config 'LogTimeIsUtc' $true)
    $offsetHours = [double](Get-ConfigValue $config 'TimeZoneOffsetHours' 0)
    $offsetMinutes = if ($logIsUtc) { [int][math]::Round($offsetHours * 60) } else { 0 }
    Write-Log ("時刻補正: {0} 分 (ログは {1})" -f $offsetMinutes, $(if ($logIsUtc) { 'UTC' } else { 'ローカル時刻' }))

    # ---- 読み込むログファイルの決定 ------------------------------------
    # 時差補正があると、ローカル日 D の行は前後の日付のファイルにまたがる。
    $fileDates = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($d in $targetDates) {
        [void]$fileDates.Add($d.ToString('yyyyMMdd'))
        if ($offsetMinutes -ne 0) {
            [void]$fileDates.Add($d.AddDays(-1).ToString('yyyyMMdd'))
            [void]$fileDates.Add($d.AddDays(1).ToString('yyyyMMdd'))
        }
    }

    $serverFilter = ConvertTo-StringArray (Get-ConfigValue $config 'Servers' @())
    $recurse = [bool](Get-ConfigValue $config 'SearchSubdirectories' $false)
    # 既定では "yyyymmdd-サーバ名.log" と "yyyymmdd_サーバ名.log" の両方を受け付ける。
    # これ以外の命名の場合は config.json の LogFileNamePattern に正規表現を書く
    # (date と server の名前付きグループが必要)。
    $namePattern = [string](Get-ConfigValue $config 'LogFileNamePattern' '^(?<date>\d{8})[-_](?<server>.+)\.log$')

    $gciParams = @{ LiteralPath = $logDir; Filter = '*.log'; File = $true }
    if ($recurse) { $gciParams['Recurse'] = $true }

    Initialize-Aggregator -Root $root

    $entryList = New-Object 'System.Collections.Generic.List[IisLogReport.LogFileEntry]'
    $totalBytes = 0L
    $scanned = 0; $nameMismatch = 0; $dateMismatch = 0; $serverMismatch = 0
    $sampleNames = New-Object System.Collections.Generic.List[string]
    $foundDates = New-Object 'System.Collections.Generic.HashSet[string]'
    $entryFileDates = @{}   # ファイルパス -> ファイル名の日付 (yyyyMMdd)
    foreach ($f in (Get-ChildItem @gciParams)) {
        $scanned++
        $m = [regex]::Match($f.Name, $namePattern, 'IgnoreCase')
        if (-not $m.Success) {
            $nameMismatch++
            if ($sampleNames.Count -lt 5) { $sampleNames.Add($f.Name) }
            continue
        }
        [void]$foundDates.Add($m.Groups['date'].Value)
        if (-not $fileDates.Contains($m.Groups['date'].Value)) { $dateMismatch++; continue }
        $server = $m.Groups['server'].Value
        if ($serverFilter.Count -gt 0 -and ($serverFilter -notcontains $server)) { $serverMismatch++; continue }
        $e = New-Object IisLogReport.LogFileEntry
        $e.Path = $f.FullName
        $e.ServerName = $server
        $entryList.Add($e)
        $entryFileDates[$f.FullName] = $m.Groups['date'].Value
        $totalBytes += $f.Length
    }

    if ($entryList.Count -eq 0) {
        # 「見つからない」で終わらせず、何件を見て何で弾いたかまで出す。
        Write-Log "対象のログファイルが見つかりませんでした: $logDir" 'WARN'
        Write-Log ("  .log ファイル {0} 件を確認 (命名不一致 {1} / 対象日以外 {2} / 対象サーバ以外 {3})" -f `
                $scanned, $nameMismatch, $dateMismatch, $serverMismatch) 'WARN'
        if ($scanned -eq 0) {
            Write-Log '  フォルダに .log ファイルがありません。LogDirectory を確認してください。' 'WARN'
            Write-Log '  サブフォルダ (W3SVC1 など) に分かれている場合は SearchSubdirectories を true にしてください。' 'WARN'
        }
        elseif ($nameMismatch -eq $scanned) {
            Write-Log ("  ファイル名の例: {0}" -f ($sampleNames -join ', ')) 'WARN'
            Write-Log ("  現在のパターン: {0}" -f $namePattern) 'WARN'
            Write-Log '  命名が異なる場合は config.json の LogFileNamePattern を調整してください (date / server の名前付きグループが必要)。' 'WARN'
        }
        elseif ($dateMismatch -gt 0) {
            $sorted = @($foundDates) | Sort-Object
            $shown = ($sorted | Select-Object -First 10) -join ', '
            if ($sorted.Count -gt 10) { $shown += ' ...' }
            Write-Log ("  フォルダにある日付: {0}" -f $shown) 'WARN'
            Write-Log ("  今回探した日付　: {0}" -f ((@($fileDates) | Sort-Object) -join ', ')) 'WARN'
        }
        elseif ($serverMismatch -gt 0) {
            Write-Log ("  config.json の Servers ({0}) に一致するファイルがありません。" -f ($serverFilter -join ', ')) 'WARN'
        }
    }
    else {
        Write-Log ("対象ログ: {0} ファイル / {1:N1} MB" -f $entryList.Count, ($totalBytes / 1MB))

        # 時差補正があると、対象日の早朝(または深夜)は隣の日付のファイルに入っている。
        # そのファイルが無いと結果が黙って欠けるので警告する。
        if ($offsetMinutes -ne 0) {
            $neighborOffset = if ($offsetMinutes -gt 0) { -1 } else { 1 }
            $missing = @($targetDates | ForEach-Object { $_.AddDays($neighborOffset).ToString('yyyyMMdd') } |
                Where-Object { -not $foundDates.Contains($_) } | Sort-Object -Unique)
            if ($missing.Count -gt 0) {
                Write-Log ("時差補正のため {0} のログも必要ですが見つかりません。対象日の一部の時間帯が欠落します。" -f ($missing -join ', ')) 'WARN'
            }
        }
    }

    # ---- オプション組み立て --------------------------------------------
    $opt = New-Object IisLogReport.AggregatorOptions
    $opt.TargetExtensions = [string[]](ConvertTo-StringArray (Get-ConfigValue $config 'TargetExtensions' @('.aspx')))
    $opt.IncludeMethods = [string[]](ConvertTo-StringArray (Get-ConfigValue $config 'IncludeMethods' @()))
    # 社内 IP とボットは行を捨てずに IsInternal / IsBot の列で印を付ける。
    # 旧設定 (ExcludeIpAddresses など) も読めるようにしておく。
    $opt.InternalIpExact = [string[]](ConvertTo-StringArray (Get-ConfigValue $config 'InternalIpAddresses' `
        (Get-ConfigValue $config 'ExcludeIpAddresses' @())))
    $opt.InternalIpCidr = [string[]](ConvertTo-StringArray (Get-ConfigValue $config 'InternalIpRanges' `
        (Get-ConfigValue $config 'ExcludeIpRanges' @())))
    $opt.BotUserAgentContains = [string[]](ConvertTo-StringArray (Get-ConfigValue $config 'BotUserAgentContains' `
        (Get-ConfigValue $config 'ExcludeUserAgentContains' @())))
    $opt.ExcludeUriPatterns = [string[]](ConvertTo-StringArray (Get-ConfigValue $config 'ExcludeUriPatterns' @()))
    $opt.SessionTimeoutMinutes = [int](Get-ConfigValue $config 'SessionTimeoutMinutes' 30)
    $opt.ClientIpFields = [string[]](ConvertTo-StringArray (Get-ConfigValue $config 'ClientIpFields' @('c-ip')))
    $opt.LowercaseUrl = [bool](Get-ConfigValue $config 'LowercaseUrl' $true)
    $opt.StripTrailingSlash = [bool](Get-ConfigValue $config 'StripTrailingSlash' $false)
    $opt.TimeZoneOffsetMinutes = $offsetMinutes
    $opt.TargetDates = [string[]]@($targetDates | ForEach-Object { $_.ToString('yyyy-MM-dd') })
    $opt.MaxDegreeOfParallelism = [int](Get-ConfigValue $config 'MaxDegreeOfParallelism' 0)

    # ---- 集計 -----------------------------------------------------------
    $result = [IisLogReport.IisLogAggregator]::Run($entryList.ToArray(), $opt)

    foreach ($err in $result.Errors) {
        Write-Log "ファイル読み込みエラー: $err" 'ERROR'
        $script:HadError = $true
    }
    Write-Log ("解析 {0:N0} 行 / 採用 {1:N0} 行 (拡張子外 {2:N0} / 条件除外 {3:N0} / 対象日外 {4:N0} / 不正 {5:N0})" -f `
            $result.LinesRead, $result.LinesCounted, $result.LinesSkippedExtension,
        $result.LinesSkippedFilter, $result.LinesSkippedOutOfRange, $result.LinesMalformed)
    $mbPerSec = if ($result.ElapsedSeconds -gt 0) { ($totalBytes / 1MB) / $result.ElapsedSeconds } else { 0 }
    Write-Log ("集計時間: {0:N1} 秒 ({1:N1} MB/秒, 並列度 {2})" -f $result.ElapsedSeconds, $mbPerSec,
        $(if ($opt.MaxDegreeOfParallelism -gt 0) { $opt.MaxDegreeOfParallelism } else { [Environment]::ProcessorCount }))

    # ---- CSV 出力 --------------------------------------------------------
    # 出すのは「足し算できる数」だけ。平均や割合は BI 側で分子 ÷ 分母として計算する。
    $encoding = [string](Get-ConfigValue $config 'OutputEncoding' 'utf8')
    $formats = @{
        request = [string](Get-ConfigValue $config 'RequestFileNameFormat' 'iis_request_{date}.csv')
        unique  = [string](Get-ConfigValue $config 'UniqueFileNameFormat' 'iis_unique_{date}.csv')
        visit   = [string](Get-ConfigValue $config 'VisitFileNameFormat' 'iis_visit_{date}.csv')
        flow    = [string](Get-ConfigValue $config 'FlowFileNameFormat' 'iis_flow_{date}.csv')
        user    = [string](Get-ConfigValue $config 'UserFileNameFormat' 'iis_user_{date}.csv')
    }
    $columns = @{
        request = @('ServerName', 'LogDate', 'PageUrl', 'PageUrlDisplay', 'Method', 'StatusClass', 'IsInternal', 'IsBot',
            'Requests', 'TimeTakenSumMs', 'TimeTakenMaxMs', 'BytesSentSum', 'T100', 'T300', 'T1000', 'T3000', 'T10000', 'TOver')
        unique  = @('Scope', 'Segment', 'ServerName', 'LogDate', 'PageUrl', 'PageUrlDisplay', 'UniqueUsers', 'UniqueIps')
        visit   = @('Scope', 'Segment', 'ServerName', 'LogDate', 'Visits', 'PageViewsInVisits', 'DistinctPagesInVisitsSum',
            'DurationSecSum', 'SinglePageVisits', 'V1', 'V2to3', 'V4to10', 'V11over', 'D0', 'D30', 'D180', 'D600', 'DOver')
        flow    = @('Scope', 'Segment', 'ServerName', 'LogDate', 'PageUrl', 'PageUrlDisplay', 'EntryCount', 'ExitCount', 'BounceCount')
        user    = @('Scope', 'Segment', 'ServerName', 'LogDate', 'Users', 'PageViewsSum', 'DistinctPagesPerUserSum',
            'Users1Page', 'Users2to5', 'Users6over')
    }

    # 日付ごとに仕分ける (CSV は日ごとに 1 本ずつ出す)
    $byDate = @{}
    foreach ($kind in 'request', 'unique', 'visit', 'flow', 'user') { $byDate[$kind] = @{} }
    $sets = @{ request = $result.Requests; unique = $result.Uniques; visit = $result.Visits; flow = $result.Flows; user = $result.Users }
    foreach ($kind in $sets.Keys) {
        foreach ($r in $sets[$kind]) {
            if (-not $byDate[$kind].ContainsKey($r.LogDate)) {
                $byDate[$kind][$r.LogDate] = New-Object System.Collections.Generic.List[object]
            }
            $byDate[$kind][$r.LogDate].Add($r)
        }
    }

    # 対象ページへのアクセスが 0 件でも、その日のログを読めたサーバは 0 の行として出す。
    # 「アクセスが無かった」と「ログが無かった」を区別するため、対象日と同じ日付の
    # ログファイルを読み込めたサーバだけを対象にする (ファイルが無い・読めない場合は行を出さない)。
    $failedPaths = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($err in $result.Errors) {
        foreach ($e in $entryList) {
            if ($err.StartsWith($e.Path + ': ', [StringComparison]::Ordinal)) { [void]$failedPaths.Add($e.Path) }
        }
    }
    $loggedServersByDate = @{}   # yyyyMMdd -> サーバ名の集合
    foreach ($e in $entryList) {
        if ($failedPaths.Contains($e.Path)) { continue }
        $fd = $entryFileDates[$e.Path]
        if (-not $loggedServersByDate.ContainsKey($fd)) {
            $loggedServersByDate[$fd] = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        }
        [void]$loggedServersByDate[$fd].Add($e.ServerName)
    }

    foreach ($d in $targetDates) {
        $iso = $d.ToString('yyyy-MM-dd')
        $stamp = $d.ToString('yyyyMMdd')

        $rows = @{}
        foreach ($kind in 'request', 'unique', 'visit', 'flow', 'user') {
            $list = New-Object System.Collections.Generic.List[object]
            if ($byDate[$kind].ContainsKey($iso)) { $list.AddRange($byDate[$kind][$iso]) }
            $rows[$kind] = $list
        }

        # アクセス 0 件のサーバの行を足す (人数・訪問・利用者の 3 本。明細と入口出口は行の作りようがない)
        $zeroCount = 0
        if ($loggedServersByDate.ContainsKey($stamp)) {
            foreach ($segment in 'all', 'human') {
                $present = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
                foreach ($r in $rows['unique']) {
                    if ($r.Scope -eq 'server-day' -and $r.Segment -eq $segment) { [void]$present.Add($r.ServerName) }
                }
                foreach ($server in $loggedServersByDate[$stamp]) {
                    if ($present.Contains($server)) { continue }
                    $zeroCount++
                    foreach ($kind in 'unique', 'visit', 'user') {
                        $z = New-Object ("IisLogReport." + $(switch ($kind) { 'unique' { 'UniqueRow' } 'visit' { 'VisitRow' } 'user' { 'UserRow' } }))
                        $z.Scope = 'server-day'; $z.Segment = $segment
                        $z.ServerName = $server; $z.LogDate = $iso
                        if ($kind -eq 'unique') { $z.PageUrl = ''; $z.PageUrlDisplay = '' }
                        $rows[$kind].Add($z)
                    }
                }
                # その日どのサーバにもアクセスが無い場合は、全サーバ合算の行も 0 で出す
                $hasAll = $false
                foreach ($r in $rows['unique']) {
                    if ($r.Scope -eq 'all-day' -and $r.Segment -eq $segment) { $hasAll = $true; break }
                }
                if (-not $hasAll) {
                    foreach ($kind in 'unique', 'visit', 'user') {
                        $z = New-Object ("IisLogReport." + $(switch ($kind) { 'unique' { 'UniqueRow' } 'visit' { 'VisitRow' } 'user' { 'UserRow' } }))
                        $z.Scope = 'all-day'; $z.Segment = $segment
                        $z.ServerName = ''; $z.LogDate = $iso
                        if ($kind -eq 'unique') { $z.PageUrl = ''; $z.PageUrlDisplay = '' }
                        $rows[$kind].Add($z)
                    }
                }
            }
            foreach ($kind in 'unique', 'visit', 'user') {
                $rows[$kind] = @($rows[$kind] | Sort-Object -Property LogDate, Segment, Scope, ServerName -CaseSensitive)
            }
        }

        foreach ($kind in 'request', 'unique', 'visit', 'flow', 'user') {
            $path = Join-Path $outDir ($formats[$kind] -replace '\{date\}', $stamp)
            if ((Test-Path -LiteralPath $path) -and -not $Force) {
                Write-Log "既に存在するためスキップします (上書きするには -Force): $path" 'WARN'
                continue
            }
            $cols = $columns[$kind]
            # 途中で落ちても壊れたファイルを残さないよう、一時ファイルに書いてから置き換える
            $tmp = "$path.tmp"
            $w = New-CsvWriter -Path $tmp -Encoding $encoding
            try {
                $w.WriteLine($cols -join ',')
                foreach ($r in $rows[$kind]) {
                    $values = New-Object System.Collections.Generic.List[string]
                    foreach ($c in $cols) {
                        $v = $r.$c
                        if ($v -is [string]) { $values.Add((Get-CsvField $v)) } else { $values.Add([string]$v) }
                    }
                    $w.WriteLine($values -join ',')
                }
            }
            finally { $w.Dispose() }
            Move-Item -LiteralPath $tmp -Destination $path -Force
            Write-Log ("出力: {0} ({1:N0} 行)" -f $path, $rows[$kind].Count)
        }
        if ($zeroCount -gt 0) { Write-Log ("アクセス 0 件のサーバを {0} 件、0 の行として出力しました。" -f $zeroCount) }
        if ($rows['request'].Count -eq 0) {
            Write-Log "$iso のリクエスト明細が 0 件でした。ログの有無と対象拡張子の設定を確認してください。" 'WARN'
        }
    }

    # ---- 古い CSV の掃除 --------------------------------------------------
    $retention = [int](Get-ConfigValue $config 'RetentionDays' 0)
    if ($retention -gt 0) {
        $limit = (Get-Date).AddDays(-$retention)
        $removed = 0
        foreach ($f in (Get-ChildItem -LiteralPath $outDir -Filter '*.csv' -File)) {
            if ($f.LastWriteTime -lt $limit) { Remove-Item -LiteralPath $f.FullName -Force; $removed++ }
        }
        if ($removed -gt 0) { Write-Log "$retention 日より古い CSV を $removed 件削除しました。" }
    }

    Write-Log '==== 集計終了 ===='
    if ($script:HadError) { exit 2 }
    exit 0
}
catch {
    Write-Log ("処理に失敗しました: {0}`n{1}" -f $_.Exception.Message, $_.ScriptStackTrace) 'ERROR'
    exit 1
}
finally {
    if ($script:LogWriter) { $script:LogWriter.Dispose() }
}
