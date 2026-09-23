# IIS アクセスログ集計 (BI 用 CSV 生成)

IIS の W3C ログから、BI ツールの元データにする日次 CSV を生成します。
タスクスケジューラで毎日前日分を自動生成する運用を想定しています。

## できること

- `yyyymmdd-<サーバ名>.log` を読み、**サーバ名 × 日 × ページ URL** 単位で集計
- **アクセス回数・人数・訪問 (セッション)・入口出口・応答時間**を日ごとに 5 本の CSV で出力
- 平均や割合は出さず、**BI 側で計算できる形 (合計と件数)** で出す
- 社内 IP・ボットは除外せず `IsInternal` / `IsBot` の列で印を付ける (判定を変えても再集計が不要)
- `.aspx` のみを対象、GET パラメータを除いた URL で名寄せ
- UTC ログを日本時間の「日」に補正 (日跨ぎのため前後日のファイルも自動で読む)

## 性能

集計本体は C# (`src\IisLogAggregator.cs`) で、ファイル単位に並列処理します。
手元の実測 (8 並列):

| 入力 | 時間 |
|---|---|
| 248 MB / 160 万行 / 4 ファイル | **2.0 秒** (約 122 MB/秒) |

数千万行でも分単位で終わる想定です。速度のための工夫:

- ファイル単位で `Parallel.ForEach`。各スレッドはローカル辞書に集計し、最後に一度だけマージ
- 「`.aspx` を含まない行」を最初に `IndexOf` で捨てる (実ログの 7 割前後がここで落ちる)
- 1 行あたりの文字列分割は必要なフィールドまでで打ち切り
- 文字列 (サーバ名 / URL / 利用者 / IP) はすべて int の ID に採番し、集計中は int だけを扱う
- 訪問の集計だけは順序が要るので、集計対象の行を並べ替えてから 1 パスで数える
  (メモリは集計対象 1 行あたり 24 バイト。400 万行なら約 100 MB)
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

## 出力の考え方

BI 側でいつでも計算し直せるように、CSV には **足し算できる数だけ** を出します。

- **平均・割合は出しません。** 分子と分母を別の列で出すので、BI 側で割って求めます。
  平均を CSV に入れてしまうと、日をまたいだ集計やサーバ横断の集計で正しい値が出せません。
  (例: 平均応答時間 = `TimeTakenSumMs ÷ Requests`)
- **人数・訪問数は足し算できません。** サーバ A の 50 人とサーバ B の 50 人を足しても 100 人には
  なりません。そのため、後から見たい粒度 (サーバ別 / 全サーバ合算) の分をあらかじめ集計して出します。
- **社内 IP・ボットは除外せず、`IsInternal` / `IsBot` の列で印を付けます。** 判定条件を見直しても
  過去分の再集計が要りません。人数や訪問は `Segment` 列で `all` (全部) と `human` (社内 IP でも
  ボットでもないアクセス) の 2 通りを出します。

この方針のため、**集計ロジックを変えずに BI 側だけで指標を増やせます。**

## 出力ファイル (日ごとに 5 本)

### 1. `iis_request_yyyymmdd.csv` — リクエスト明細の集計

粒度は **サーバ × 日 × ページ × メソッド × ステータス区分 × IsInternal × IsBot** です。
すべて足し算できる数なので、ここから PV・エラー率・応答時間などが作れます。

| 列 | 内容 |
|---|---|
| ServerName | ログのファイル名から取得したサーバ名 |
| LogDate | 日付 (`yyyy-MM-dd`、時差補正後) |
| PageUrl | 名寄せ後のページ URL。クエリ文字列を除いたパス (`LowercaseUrl` が `true` なら小文字) |
| PageUrlDisplay | 表示用の URL。ログに最も多く現れた元の表記 |
| Method | `GET` / `POST` など |
| StatusClass | `2xx` / `3xx` / `4xx` / `5xx` / `other` |
| IsInternal | 社内 IP からのアクセスなら `1` |
| IsBot | UA がボットと判定されたら `1` |
| Requests | 件数 (PV) |
| TimeTakenSumMs | 応答時間の合計。平均は `÷ Requests` |
| TimeTakenMaxMs | 最大応答時間 |
| BytesSentSum | 応答サイズの合計 (`sc-bytes`。ログに無い場合は 0) |
| T100 / T300 / T1000 / T3000 / T10000 / TOver | 応答時間の分布 (件数)。順に 100ms 未満 / 300ms 未満 / 1 秒未満 / 3 秒未満 / 10 秒未満 / 10 秒以上 |

応答時間の分布は、中央値や 90 パーセンタイルの代わりです。パーセンタイルは足し算できないので、
区間ごとの件数で持っておき、「3 秒以上かかった割合」のような形で BI 側から見ます。

### 2. `iis_unique_yyyymmdd.csv` — 人数

| 列 | 内容 |
|---|---|
| Scope | `server-day` / `server-page` / `all-day` / `all-page` |
| Segment | `all` / `human` |
| ServerName | `all-*` の行では空 |
| LogDate | 日付 |
| PageUrl / PageUrlDisplay | `*-day` の行では空 |
| UniqueUsers | 人数。`cs-username` があればユーザー、なければ IP で数える |
| UniqueIps | ユニーク IP 数 |

**ロードバランサで複数台に振り分けている場合、サーバ別の人数を足しても全体の人数にはなりません。**
全体を見るときは `all-day` / `all-page` の行を使ってください。週・月の人数も足し算では求まらないので、
必要なら `-From` / `-To` でその期間を集計し直してください。

### 3. `iis_visit_yyyymmdd.csv` — 訪問 (セッション)

同じ利用者のアクセスが `SessionTimeoutMinutes` (既定 30 分) 以上空いたら、別の訪問として数えます。

| 列 | 内容 |
|---|---|
| Scope / Segment / ServerName / LogDate | `Scope` は `server-day` / `all-day` |
| Visits | 訪問回数 |
| PageViewsInVisits | 訪問中の総閲覧数。`÷ Visits` で 1 訪問あたりのページ数 |
| DistinctPagesInVisitsSum | 訪問ごとの「見たページの種類数」の合計 |
| DurationSecSum | 滞在時間の合計 (秒)。`÷ Visits` で平均滞在時間 |
| SinglePageVisits | 1 ページだけで終わった訪問数。`÷ Visits` で直帰率 |
| V1 / V2to3 / V4to10 / V11over | 1 訪問あたり閲覧数の分布 |
| D0 / D30 / D180 / D600 / DOver | 滞在時間の分布。順に 30 秒未満 / 3 分未満 / 10 分未満 / 30 分未満 / 30 分以上 |

**滞在時間はログから厳密には分かりません。** IIS のログに残るのはリクエストだけで、ページを
表示していた時間は記録されないためです。ここでの滞在時間は「その訪問の最初のリクエストから
最後のリクエストまで」です。最後に見たページの時間は含まれないので、実際より短めに出ます
(1 ページだけの訪問は 0 秒になります)。一般的なアクセス解析ツールも同じ数え方です。

### 4. `iis_flow_yyyymmdd.csv` — ページの入口・出口

| 列 | 内容 |
|---|---|
| Scope / Segment / ServerName / LogDate | `Scope` は `server-page` / `all-page` |
| PageUrl / PageUrlDisplay | ページ |
| EntryCount | そのページから訪問が始まった回数 |
| ExitCount | そのページで訪問が終わった回数 |
| BounceCount | そのページだけ見て終わった回数 |

### 5. `iis_user_yyyymmdd.csv` — 利用者の広がり

| 列 | 内容 |
|---|---|
| Scope / Segment / ServerName / LogDate | `Scope` は `server-day` / `all-day` |
| Users | 利用者数 (`iis_unique` の `UniqueUsers` と同じ) |
| PageViewsSum | 利用者の総閲覧数。`÷ Users` で 1 人あたり PV |
| DistinctPagesPerUserSum | 利用者ごとの「見たページの種類数」の合計。`÷ Users` で 1 人あたりページ種類数 |
| Users1Page / Users2to5 / Users6over | 利用者ごとの閲覧ページ種類数の分布 |

### アクセスが 0 件のサーバ

**対象ページへのアクセスが 0 件でも、その日のログを読めたサーバは 0 の行として出力します**
(`iis_unique` / `iis_visit` / `iis_user` の `server-day` の行)。
対象は「その日付のログファイル (例 `20260916-WEB01.log`) を読み込めたサーバ」です。
ログファイルが無い・読めなかったサーバは、アクセス 0 件と区別するため行を出しません。

### URL の名寄せと表記について

IIS (Windows) の URL は大文字小文字を区別しないため、同じページでもログには
`/Order/List.aspx` と `/order/list.aspx` が混在します。そのまま数えると 1 つのページが
複数行に分かれてしまうので、**小文字に揃えたものを集計キー (`PageUrl`) にしています。**

ただし小文字だけだと読みにくいので、**ログに最も多く現れた元の表記を `PageUrlDisplay` に残します。**

| PageUrl | PageUrlDisplay | Requests |
|---|---|---|
| `/order/list.aspx` | `/Order/List.aspx` | 167 |
| `/report/summary.aspx` | `/report/Summary.aspx` | 182 |

BI では **`PageUrl` でグループ化し、`PageUrlDisplay` を表示に使う**のが安全です
(表記が揺れても集計は 1 行にまとまります)。名寄せ自体が不要なら `LowercaseUrl` を
`false` にしてください。その場合は表記ごとに別行として出力されます。

## BI 側での計算例

| 見たい指標 | 計算 | 使うファイル |
|---|---|---|
| PV 数 | `Requests` の合計 | request |
| エラー率 | `StatusClass` が `4xx`/`5xx` の `Requests` ÷ 全体 | request |
| 平均応答時間 | `TimeTakenSumMs` の合計 ÷ `Requests` の合計 | request |
| 3 秒以上かかった割合 | (`T3000` + `T10000` + `TOver`) ÷ `Requests` | request |
| 利用者数 | `UniqueUsers` (足さずにその行の値を使う) | unique |
| 1 人あたり PV | `PageViewsSum` ÷ `Users` | user |
| 1 訪問あたりページ数 | `PageViewsInVisits` ÷ `Visits` | visit |
| 平均滞在時間 | `DurationSecSum` ÷ `Visits` | visit |
| 直帰率 | `SinglePageVisits` ÷ `Visits` | visit |
| 社内からの利用比率 | `IsInternal` が `1` の `Requests` ÷ 全体 | request |
| よく使われる入口ページ | `EntryCount` の降順 | flow |

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
| `IncludeMethods` | `[]` | 対象 HTTP メソッド。空なら全部 (`Method` 列で BI 側から絞れます) |
| `LowercaseUrl` | `true` | URL を小文字に寄せて名寄せする。元の表記は `PageUrlDisplay` 列に残る |
| `StripTrailingSlash` | `false` | 末尾スラッシュを落として名寄せする |
| `InternalIpAddresses` | `["127.0.0.1","::1"]` | 社内とみなす IP (完全一致。IPv6 可)。`IsInternal=1` になる |
| `InternalIpRanges` | 社内 IP 帯 | 社内とみなす IP レンジ (IPv4 CIDR) |
| `ClientIpFields` | `["c-ip"]` | クライアント IP に使うフィールド。ロードバランサ配下なら `["cs(X-Forwarded-For)","c-ip"]` |
| `BotUserAgentContains` | bot 各種 | UA にこの文字列を含む行に `IsBot=1` を立てる |
| `ExcludeUriPatterns` | `elmah.axd` など | URL がこの正規表現に一致する行を集計から完全に外す |
| `SessionTimeoutMinutes` | `30` | この時間が空いたら別の訪問とみなす |
| `LogTimeIsUtc` | `true` | IIS の既定は UTC 記録。ローカル時刻で記録している場合は `false` |
| `TimeZoneOffsetHours` | `9` | UTC からの補正時間 (日本は 9) |
| `RequestFileNameFormat` ほか | `iis_request_{date}.csv` など | 各 CSV のファイル名 (`Unique` / `Visit` / `Flow` / `User`) |
| `OutputEncoding` | `utf8` | `utf8` (BOM 付き) / `utf8nobom` / `shift_jis` |
| `RetentionDays` | `0` | 出力先の古い CSV を自動削除する日数。`0` で削除しない |
| `MaxDegreeOfParallelism` | `0` | 並列度。`0` で CPU 数。本番機の負荷を抑えたいときに下げる |

旧設定の `ExcludeIpAddresses` / `ExcludeIpRanges` / `ExcludeUserAgentContains` も読み込みますが、
除外ではなく `IsInternal` / `IsBot` の判定に使います。

### ログのファイル名が違う場合

既定では `20260129-WEB01.log` と `20260129_t8701r.log` の両方を読みます。
これ以外の命名なら `LogFileNamePattern` に正規表現を書いてください
(**`date` と `server` の名前付きグループが必須**です)。JSON なので `\` は `\\` と書きます。

```json
"LogFileNamePattern": "^(?<server>[a-z0-9]+)\\.(?<date>\\d{8})\\.log$"
```

対象ファイルが 0 件のときは、何件を見て何で弾いたか (命名不一致 / 対象日以外 / 対象サーバ以外)、
実際のファイル名の例、フォルダにある日付を警告に出すので、そこから原因を特定できます。

### 社内 IP の指定例

```json
"InternalIpAddresses": ["203.0.113.10", "203.0.113.11"],
"InternalIpRanges": ["10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12"]
```

IPv6 は完全一致のみ対応です (CIDR は IPv4 のみ)。

## 集計の前提 (ここを変えると再集計が必要)

以下はログの読み方そのものなので、後から変えると過去分の作り直しが必要です。

- **クエリ文字列は捨てます。** `/view.aspx?id=1` と `/view.aspx?id=2` は同じページとして数えます
- **利用者の識別**は `cs-username`、無ければクライアント IP です
- **訪問の区切りは 30 分**、かつ日をまたぐと別の訪問として数えます
- **日付は時差補正後のローカル日**です (`TimeZoneOffsetHours`)

## BI ツールへの取り込み

出力先フォルダを丸ごとデータソースにするのが簡単です。

- **Power BI**: 「フォルダーから」→ `iis_request_*.csv` を結合。同じように `iis_unique_*` / `iis_visit_*` /
  `iis_flow_*` / `iis_user_*` も別テーブルとして取り込みます。日次で追加されたファイルは更新時に自動で入ります
- 日付は `LogDate` (`yyyy-MM-dd`) をそのまま日付型に変換できます
- ページはグループ化に `PageUrl`、表示に `PageUrlDisplay` を使ってください
- **人数・訪問のテーブルでは、必ず `Scope` と `Segment` で絞ってから使ってください。**
  絞らずに合計すると、サーバ別と全サーバ合算、`all` と `human` が二重に足されます

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
