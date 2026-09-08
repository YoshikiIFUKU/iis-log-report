# IIS アクセスログ集計 (BI 用 CSV 生成)

IIS の W3C ログから、BI ツールの元データにする日次 CSV を生成します。
タスクスケジューラで毎日前日分を自動生成する運用を想定しています。

## できること

- `yyyymmdd-<サーバ名>.log` を読み、**サーバ名 × 日 × ページ URL** 単位で集計
- **アクセス回数 (PageViews)** と **アクセス人数 (UniqueUsers / UniqueIps)** を出力
- 特定 IP / IP レンジ (CIDR) からのアクセスを除外
- `.aspx` のみを対象、GET パラメータを除いた URL で名寄せ
- UTC ログを日本時間の「日」に補正 (日跨ぎのため前後日のファイルも自動で読む)

## 性能

集計本体は C# (`src\IisLogAggregator.cs`) で、ファイル単位に並列処理します。
手元の実測 (8 並列):

| 入力 | 時間 |
|---|---|
| 580 MB / 400 万行 / 8 ファイル | **3.7 秒** (約 160 MB/秒) |

数千万行でも分単位で終わる想定です。速度のための工夫:

- ファイル単位で `Parallel.ForEach`。各スレッドはローカル辞書に集計し、最後に一度だけマージ
- 「`.aspx` を含まない行」を最初に `IndexOf` で捨てる (実ログの 7 割前後がここで落ちる)
- 1 行あたりの文字列分割は必要なフィールドまでで打ち切り
- ユニーク数は IP を int の ID に採番して `HashSet<int>` で保持 (メモリ削減)
- C# は初回のみコンパイルして `bin\IisLogAggregator.dll` にキャッシュ

## セットアップ

1. このフォルダをサーバに配置する (例: `C:\Tools\iis-log-report`)
2. `config.json` を環境に合わせて編集する
3. 動作確認する

   ```powershell
   .\New-IisAccessReport.ps1 -Date 20260907 -Force
   ```

4. 日次タスクを登録する (管理者権限の PowerShell)

   ```powershell
   .\Register-DailyTask.ps1 -At 02:00
   ```

## 使い方

```powershell
# 前日分 (タスクスケジューラからの通常運用)
.\New-IisAccessReport.ps1

# 特定日を作り直す
.\New-IisAccessReport.ps1 -Date 20260907 -Force

# 過去分をまとめて初期ロード (日ごとに CSV を 1 本ずつ出力)
.\New-IisAccessReport.ps1 -From 20260801 -To 20260831

# 設定ファイルやフォルダを一時的に差し替える
.\New-IisAccessReport.ps1 -ConfigPath D:\conf\prod.json -OutputDirectory D:\share\bi
```

終了コード: `0` 正常 / `1` 異常終了 / `2` 一部ファイルの読み込みに失敗 (監視で拾えます)。

実行ログは `<OutputDirectory>\logs\run_yyyyMM.log` に追記されます。

## 出力

### `iis_page_yyyymmdd.csv` — ページ別

| 列 | 内容 |
|---|---|
| ServerName | ログのファイル名から取得したサーバ名 |
| LogDate | 日付 (`yyyy-MM-dd`、時差補正後) |
| PageUrl | 名寄せ後のページ URL。`http(s)://サーバ名` とクエリ文字列を除いたパス (`LowercaseUrl` が `true` なら小文字) |
| PageUrlDisplay | 表示用の URL。ログに最も多く現れた元の表記をそのまま出す |
| PageViews | アクセス回数 |
| UniqueUsers | アクセス人数。`cs-username` があればユーザー、なければ IP で数える |
| UniqueIps | ユニーク IP 数 |
| AvgTimeTakenMs | 平均応答時間 (ミリ秒) |
| MaxTimeTakenMs | 最大応答時間 (ミリ秒) |
| ErrorCount | ステータス 400 以上の件数 |

#### URL の名寄せと表記について

IIS (Windows) の URL は大文字小文字を区別しないため、同じページでもログには
`/Order/List.aspx` と `/order/list.aspx` が混在します。そのまま数えると 1 つのページが
複数行に分かれてしまうので、**小文字に揃えたものを集計キー (`PageUrl`) にしています。**

ただし小文字だけだと読みにくいので、**ログに最も多く現れた元の表記を `PageUrlDisplay` に残します。**

| PageUrl | PageUrlDisplay | PageViews |
|---|---|---|
| `/order/list.aspx` | `/Order/List.aspx` | 167 |
| `/report/summary.aspx` | `/report/Summary.aspx` | 182 |

BI では **`PageUrl` でグループ化し、`PageUrlDisplay` を表示に使う**のが安全です
(表記が揺れても集計は 1 行にまとまります)。名寄せ自体が不要なら `LowercaseUrl` を
`false` にしてください。その場合は表記ごとに別行として出力されます。

### `iis_daily_yyyymmdd.csv` — サーバ × 日のサマリ

**ユニーク人数はページ別 CSV を足し上げても正しく求まりません** (同じ人が複数ページを見るため)。
BI 側で「その日の利用者数」を正しく出せるよう、日単位の実測値を別ファイルで持ちます。

| 列 | 内容 |
|---|---|
| ServerName / LogDate | 同上 |
| PageViews | その日の総アクセス回数 |
| UniqueUsers / UniqueIps | その日の実利用者数 (ページ横断で重複排除済み) |
| PageCount | 閲覧されたページ URL の種類数 |
| AvgTimeTakenMs / ErrorCount | 平均応答時間 / エラー件数 |

## 設定 (`config.json`)

> **パスの書き方に注意**
> JSON ではバックスラッシュがエスケープ文字です。`"C:\inetpub\logs"` と 1 つで書くと
> `\i` が不正なエスケープとみなされ、「認識できないエスケープシーケンスです」で失敗します。
>
> ```json
> "LogDirectory": "C:\\inetpub\\logs\\LogFiles",   ← \ を 2 つ重ねる
> "LogDirectory": "C:/inetpub/logs/LogFiles",      ← または / で書く
> ```
>
> (スクリプト側でも自動補正して読み込みますが、警告が出ます。上記の書き方に直してください)

| キー | 既定値 | 説明 |
|---|---|---|
| `LogDirectory` | — | ログの置き場所 |
| `SearchSubdirectories` | `false` | サブフォルダも探すか (`W3SVC1` などに分かれている場合は `true`) |
| `LogFileNamePattern` | `^(?<date>\d{8})[-_](?<server>.+)\.log$` | ログのファイル名。既定で `20260129-WEB01.log` と `20260129_t8701r.log` の両方に対応 |
| `OutputDirectory` | — | CSV の出力先 |
| `Servers` | `[]` | 対象サーバ名。空なら全サーバ |
| `TargetExtensions` | `[".aspx"]` | 対象拡張子。空にすると全 URL |
| `IncludeMethods` | `["GET","POST"]` | 対象 HTTP メソッド。空なら全部 |
| `ExcludeStatusCodes` | `[]` | 集計から外すステータス (例 `[404]`) |
| `LowercaseUrl` | `true` | URL を小文字に寄せて名寄せする。元の表記は `PageUrlDisplay` 列に残る |
| `StripTrailingSlash` | `false` | 末尾スラッシュを落として名寄せする |
| `ExcludeIpAddresses` | `["127.0.0.1","::1"]` | 除外する IP (完全一致。IPv6 可) |
| `ExcludeIpRanges` | 社内 IP 帯 | 除外する IP レンジ (IPv4 CIDR) |
| `ClientIpFields` | `["c-ip"]` | クライアント IP に使うフィールド。ロードバランサ配下なら `["cs(X-Forwarded-For)","c-ip"]` |
| `ExcludeUserAgentContains` | bot 各種 | UA にこの文字列を含む行を除外 |
| `ExcludeUriPatterns` | `elmah.axd` など | URL がこの正規表現に一致する行を除外 |
| `LogTimeIsUtc` | `true` | IIS の既定は UTC 記録。ローカル時刻で記録している場合は `false` |
| `TimeZoneOffsetHours` | `9` | UTC からの補正時間 (日本は 9) |
| `PageFileNameFormat` | `iis_page_{date}.csv` | ページ別 CSV のファイル名 |
| `DailyFileNameFormat` | `iis_daily_{date}.csv` | サマリ CSV のファイル名 |
| `WriteDailySummary` | `true` | サマリ CSV を出すか |
| `OutputEncoding` | `utf8` | `utf8` (BOM 付き) / `utf8nobom` / `shift_jis` |
| `RetentionDays` | `0` | 出力先の古い CSV を自動削除する日数。`0` で削除しない |
| `MaxDegreeOfParallelism` | `0` | 並列度。`0` で CPU 数。本番機の負荷を抑えたいときに下げる |

### ログのファイル名が違う場合

既定では `20260129-WEB01.log` と `20260129_t8701r.log` の両方を読みます。
これ以外の命名なら `LogFileNamePattern` に正規表現を書いてください
(**`date` と `server` の名前付きグループが必須**です)。JSON なので `\` は `\\` と書きます。

```json
"LogFileNamePattern": "^(?<server>[a-z0-9]+)\\.(?<date>\\d{8})\\.log$"
```

対象ファイルが 0 件のときは、何件を見て何で弾いたか (命名不一致 / 対象日以外 / 対象サーバ以外)、
実際のファイル名の例、フォルダにある日付を警告に出すので、そこから原因を特定できます。

### 除外 IP の指定例

```json
"ExcludeIpAddresses": ["203.0.113.10", "203.0.113.11"],
"ExcludeIpRanges": ["10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12"]
```

社内からのアクセスを除いて「社外の利用状況」を見たい、監視ツールの死活監視を除きたい、
といった用途を想定しています。IPv6 は完全一致のみ対応です (CIDR は IPv4 のみ)。

## BI ツールへの取り込み

出力先フォルダを丸ごとデータソースにするのが簡単です。

- **Power BI**: 「フォルダーから」→ `iis_page_*.csv` を結合。日次で追加されたファイルは更新時に自動で取り込まれます
- 日付は `LogDate` (`yyyy-MM-dd`) をそのまま日付型に変換できます
- ページはグループ化に `PageUrl`、表示に `PageUrlDisplay` を使ってください
- 人数を見るときは `iis_daily_*.csv` を使ってください (ページ別の `UniqueUsers` は合計できません)

## 運用上の注意

- **CSV は一時ファイルに書いてから置き換え**ます。BI が読んでいる最中に壊れたファイルを掴む事故を防ぐためです
- 同じ日の CSV が既にある場合はスキップします。作り直すときは `-Force` を付けてください
- ログは IIS が書き込み中でも読めます (共有読み取りで開いています)
- IIS のログローテーションが毎時になっていると `yyyymmdd-<サーバ名>.log` の命名にならないため、
  日次ローテーション、またはログを集約する際にこの命名に揃えてください
- `bin\` は自動生成物なのでバージョン管理に含める必要はありません

## 動作確認用のダミーログ

```powershell
.\tools\New-SampleIisLog.ps1 -OutputDirectory C:\temp\iislogs -Date 20260907 -Servers WEB01,WEB02 -LinesPerFile 200000
```

静的ファイル 7 割・社内 IP 1 割・bot UA 混在という、実ログに近い比率で生成します。

## ファイル構成

```
iis-log-report/
├─ New-IisAccessReport.ps1   メインスクリプト
├─ Register-DailyTask.ps1    タスクスケジューラ登録
├─ config.json               設定
├─ src/IisLogAggregator.cs   集計エンジン (C#)
├─ tools/New-SampleIisLog.ps1 ダミーログ生成
└─ bin/                      コンパイル済み DLL (自動生成)
```
