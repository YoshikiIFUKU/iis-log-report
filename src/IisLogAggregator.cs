// IIS W3C ログの高速集計エンジン。
// PowerShell 側から Add-Type で読み込んで使う (C# 5 / .NET Framework 4.5 互換で記述)。
//
// 出力の考え方:
//   * CSV には「足し算できる数」だけを出す。平均や割合は BI 側で分子 ÷ 分母として計算する。
//     (平均を CSV に入れると、日をまたいだ集計やサーバ横断の集計で正しい値が出せなくなる)
//   * 人数・訪問数は足し算できないので、後から見たい粒度 (サーバ別 / 全サーバ合算) の分だけ
//     あらかじめ集計して出す。
//   * 社内 IP・ボットは行を捨てずに IsInternal / IsBot の列で印を付ける。判定条件を見直しても
//     過去分の再集計が要らないようにするため。
//
// 速度のための設計:
//   * ファイル単位で Parallel.ForEach。各スレッドはローカル辞書に集計し、最後に一度だけマージする。
//   * 1 行につき文字列 Split を行わず、必要なフィールドだけを 1 パスで切り出す。
//   * 最初に「対象拡張子を含まない行」を IndexOf で捨てる。IIS ログの大半は静的ファイルなので、
//     ここで落ちる行はこれ以降の処理を一切行わない (実測でこれが最も効く)。
//   * 文字列 (サーバ名 / 日付 / URL / 利用者 / IP) はすべて int の ID に採番し、集計中は int だけを扱う。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IisLogReport
{
    /// <summary>集計対象の 1 ファイル。</summary>
    public class LogFileEntry
    {
        public string Path;
        public string ServerName;
    }

    /// <summary>集計の挙動を決める設定。PowerShell 側の config.json から組み立てる。</summary>
    public class AggregatorOptions
    {
        public string[] TargetExtensions = new string[] { ".aspx" };
        public string[] IncludeMethods = new string[0];       // 空なら全メソッド (Method 列で絞れるので既定は空)
        public string[] InternalIpExact = new string[0];      // IsInternal=1 を立てる IP (完全一致)
        public string[] InternalIpCidr = new string[0];       // IsInternal=1 を立てる IP レンジ (IPv4 CIDR)
        public string[] BotUserAgentContains = new string[0]; // IsBot=1 を立てる UA の部分文字列
        public string[] ExcludeUriPatterns = new string[0];   // 集計から完全に外す URL (正規表現)
        public string[] ClientIpFields = new string[] { "c-ip" };
        public bool LowercaseUrl = true;
        public bool StripTrailingSlash = false;
        public int TimeZoneOffsetMinutes = 0;
        public string[] TargetDates = new string[0];          // "yyyy-MM-dd" (ローカル日付)。空なら全日付
        public int SessionTimeoutMinutes = 30;                // この時間が空いたら別の訪問とみなす
        public int MaxDegreeOfParallelism = 0;                // 0 なら CPU 数
    }

    /// <summary>リクエスト明細の集計 1 行。すべて足し算できる数だけを持つ。</summary>
    public class RequestRow
    {
        public string ServerName;
        public string LogDate;
        public string PageUrl;         // 名寄せ後のキー (LowercaseUrl が true なら小文字)
        public string PageUrlDisplay;  // 表示用。ログに最も多く現れた元の表記
        public string Method;
        public string StatusClass;     // 2xx / 3xx / 4xx / 5xx / other
        public int IsInternal;
        public int IsBot;
        public long Requests;
        public long TimeTakenSumMs;
        public int TimeTakenMaxMs;
        public long BytesSentSum;
        public long T100, T300, T1000, T3000, T10000, TOver;   // 応答時間の分布 (件数)
    }

    /// <summary>人数。足し算できないので粒度 (Scope) ごとに 1 行ずつ出す。</summary>
    public class UniqueRow
    {
        public string Scope;      // server-day / server-page / all-day / all-page
        public string Segment;    // all / human
        public string ServerName; // all-* では空
        public string LogDate;
        public string PageUrl;    // *-day では空
        public string PageUrlDisplay;
        public int UniqueUsers;
        public int UniqueIps;
    }

    /// <summary>訪問 (セッション) の集計。平均は出さず、合計と件数で持つ。</summary>
    public class VisitRow
    {
        public string Scope;      // server-day / all-day
        public string Segment;
        public string ServerName;
        public string LogDate;
        public long Visits;
        public long PageViewsInVisits;
        public long DistinctPagesInVisitsSum;
        public long DurationSecSum;
        public long SinglePageVisits;
        public long V1, V2to3, V4to10, V11over;   // 1 訪問あたり閲覧数の分布
        public long D0, D30, D180, D600, DOver;   // 滞在時間の分布 (秒)
    }

    /// <summary>ページごとの入口 / 出口。</summary>
    public class FlowRow
    {
        public string Scope;      // server-page / all-page
        public string Segment;
        public string ServerName;
        public string LogDate;
        public string PageUrl;
        public string PageUrlDisplay;
        public long EntryCount;
        public long ExitCount;
        public long BounceCount;
    }

    /// <summary>利用者ごとの広がり。平均は出さず、合計と人数で持つ。</summary>
    public class UserRow
    {
        public string Scope;      // server-day / all-day
        public string Segment;
        public string ServerName;
        public string LogDate;
        public long Users;
        public long PageViewsSum;
        public long DistinctPagesPerUserSum;
        public long Users1Page, Users2to5, Users6over;
    }

    public class AggregateResult
    {
        public RequestRow[] Requests = new RequestRow[0];
        public UniqueRow[] Uniques = new UniqueRow[0];
        public VisitRow[] Visits = new VisitRow[0];
        public FlowRow[] Flows = new FlowRow[0];
        public UserRow[] Users = new UserRow[0];
        public long LinesRead;
        public long LinesCounted;
        public long LinesSkippedExtension;
        public long LinesSkippedFilter;
        public long LinesSkippedOutOfRange;
        public long LinesMalformed;
        public int FilesRead;
        public string[] Errors = new string[0];
        public double ElapsedSeconds;
    }

    // ---- 内部用 ----

    /// <summary>リクエスト明細の集計キー。文字列を組み立てず int の組で持つ。</summary>
    internal struct ReqKey : IEquatable<ReqKey>
    {
        public int Server, Date, Page, Method;
        public byte StatusClass, Flags;   // Flags: bit0=IsInternal, bit1=IsBot

        public bool Equals(ReqKey o)
        {
            return Server == o.Server && Date == o.Date && Page == o.Page
                && Method == o.Method && StatusClass == o.StatusClass && Flags == o.Flags;
        }
        public override bool Equals(object o) { return o is ReqKey && Equals((ReqKey)o); }
        public override int GetHashCode()
        {
            int h = Server;
            h = h * 397 ^ Date; h = h * 397 ^ Page; h = h * 397 ^ Method;
            h = h * 397 ^ StatusClass; h = h * 397 ^ Flags;
            return h;
        }
    }

    internal class ReqBucket
    {
        public long Requests, TimeSum, Bytes;
        public int TimeMax;
        public long T100, T300, T1000, T3000, T10000, TOver;

        public void Add(int taken, int bytes)
        {
            Requests++;
            TimeSum += taken;
            Bytes += bytes;
            if (taken > TimeMax) TimeMax = taken;
            if (taken < 100) T100++;
            else if (taken < 300) T300++;
            else if (taken < 1000) T1000++;
            else if (taken < 3000) T3000++;
            else if (taken < 10000) T10000++;
            else TOver++;
        }

        public void Merge(ReqBucket o)
        {
            Requests += o.Requests; TimeSum += o.TimeSum; Bytes += o.Bytes;
            if (o.TimeMax > TimeMax) TimeMax = o.TimeMax;
            T100 += o.T100; T300 += o.T300; T1000 += o.T1000;
            T3000 += o.T3000; T10000 += o.T10000; TOver += o.TOver;
        }
    }

    /// <summary>訪問・人数の集計に使う 1 アクセス。集計後に破棄する。</summary>
    internal struct EventRec
    {
        public int User;
        public int Ip;
        public int Page;
        public int Time;     // 2000-01-01 からの秒 (ローカル時刻に補正済み)
        public short Server;
        public short Date;
        public byte Human;   // 社内 IP でもボットでもない = 1
    }

    internal class Counters
    {
        public long Read, Counted, SkipExt, SkipFilter, SkipRange, Malformed;
    }

    public static class IisLogAggregator
    {
        private static readonly DateTime Epoch = new DateTime(2000, 1, 1);

        // 文字列 -> int ID。全スレッド共有。集計中は int だけを扱う。
        private static ConcurrentDictionary<string, int> _pageIds, _userIds, _ipIds, _methodIds, _dateIds, _serverIds;
        private static int _nextPage, _nextUser, _nextIp, _nextMethod, _nextDate, _nextServer;

        private static int GetId(ConcurrentDictionary<string, int> map, string value, ref int next)
        {
            int id;
            if (map.TryGetValue(value, out id)) return id;
            return map.GetOrAdd(value, System.Threading.Interlocked.Increment(ref next) - 1);
        }

        private static string[] ToNameArray(ConcurrentDictionary<string, int> map)
        {
            // 採番が競合すると ID に欠番が出ることがあるので、最大 ID に合わせて確保する。
            int max = -1;
            foreach (var kv in map) if (kv.Value > max) max = kv.Value;
            var names = new string[max + 1];
            foreach (var kv in map) names[kv.Value] = kv.Key;
            return names;
        }

        public static AggregateResult Run(LogFileEntry[] files, AggregatorOptions o)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _pageIds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal); _nextPage = 0;
            _userIds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal); _nextUser = 0;
            _ipIds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal); _nextIp = 0;
            _methodIds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal); _nextMethod = 0;
            _dateIds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal); _nextDate = 0;
            _serverIds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal); _nextServer = 0;

            var extensions = o.TargetExtensions == null ? new string[0] : o.TargetExtensions;
            var methods = new HashSet<string>(o.IncludeMethods ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var internalExact = new HashSet<string>(o.InternalIpExact ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var internalCidrs = ParseCidrs(o.InternalIpCidr);
            var botNeedles = o.BotUserAgentContains ?? new string[0];
            var uriRegexes = new List<Regex>();
            foreach (var p in (o.ExcludeUriPatterns ?? new string[0]))
                uriRegexes.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant));
            var targetDates = new HashSet<string>(o.TargetDates ?? new string[0], StringComparer.Ordinal);
            var clientIpFields = (o.ClientIpFields == null || o.ClientIpFields.Length == 0)
                ? new string[] { "c-ip" } : o.ClientIpFields;

            // 社内 IP 判定の結果キャッシュ。同じ IP が何万回も出るので効く。
            var internalVerdict = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

            var mergedReq = new Dictionary<ReqKey, ReqBucket>();
            var mergedVariants = new Dictionary<int, Dictionary<string, long>>();
            var events = new List<EventRec>();
            var total = new Counters();
            var errors = new ConcurrentBag<string>();
            int filesRead = 0;
            object gate = new object();

            var po = new ParallelOptions();
            po.MaxDegreeOfParallelism = o.MaxDegreeOfParallelism > 0
                ? o.MaxDegreeOfParallelism : Environment.ProcessorCount;

            Parallel.ForEach(files, po, file =>
            {
                var localReq = new Dictionary<ReqKey, ReqBucket>();
                var localVariants = new Dictionary<int, Dictionary<string, long>>();
                var localEvents = new List<EventRec>();
                var c = new Counters();
                try
                {
                    ProcessFile(file, o, extensions, methods, internalExact, internalCidrs, botNeedles,
                                uriRegexes, targetDates, clientIpFields, internalVerdict,
                                localReq, localVariants, localEvents, c);
                    System.Threading.Interlocked.Increment(ref filesRead);
                }
                catch (Exception ex)
                {
                    errors.Add(file.Path + ": " + ex.Message);
                }

                lock (gate)
                {
                    foreach (var kv in localReq)
                    {
                        ReqBucket b;
                        if (!mergedReq.TryGetValue(kv.Key, out b)) mergedReq[kv.Key] = kv.Value;
                        else b.Merge(kv.Value);
                    }
                    foreach (var kv in localVariants)
                    {
                        Dictionary<string, long> dst;
                        if (!mergedVariants.TryGetValue(kv.Key, out dst)) { mergedVariants[kv.Key] = kv.Value; continue; }
                        foreach (var v in kv.Value)
                        {
                            long n; dst.TryGetValue(v.Key, out n); dst[v.Key] = n + v.Value;
                        }
                    }
                    events.AddRange(localEvents);
                    total.Read += c.Read; total.Counted += c.Counted; total.SkipExt += c.SkipExt;
                    total.SkipFilter += c.SkipFilter; total.SkipRange += c.SkipRange; total.Malformed += c.Malformed;
                }
            });

            var pageNames = ToNameArray(_pageIds);
            var methodNames = ToNameArray(_methodIds);
            var dateNames = ToNameArray(_dateIds);
            var serverNames = ToNameArray(_serverIds);
            var displayNames = BuildDisplayNames(pageNames, mergedVariants);

            var result = new AggregateResult();
            result.Requests = BuildRequestRows(mergedReq, serverNames, dateNames, pageNames, displayNames, methodNames);

            // 訪問・人数はアクセスの並び順が要るので、ここからは単一スレッドで処理する。
            var arr = events.ToArray();
            events = null;
            var sink = new RowSink();
            int timeoutSec = Math.Max(1, o.SessionTimeoutMinutes) * 60;
            SortAndScan(arr, true, timeoutSec, sink);    // サーバ別
            SortAndScan(arr, false, timeoutSec, sink);   // 全サーバ合算
            sink.Emit(result, serverNames, dateNames, pageNames, displayNames);

            result.LinesRead = total.Read;
            result.LinesCounted = total.Counted;
            result.LinesSkippedExtension = total.SkipExt;
            result.LinesSkippedFilter = total.SkipFilter;
            result.LinesSkippedOutOfRange = total.SkipRange;
            result.LinesMalformed = total.Malformed;
            result.FilesRead = filesRead;
            result.Errors = errors.ToArray();
            _pageIds = _userIds = _ipIds = _methodIds = _dateIds = _serverIds = null; // ID テーブルは大きいので GC に返す
            sw.Stop();
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        // ---------------------------------------------------------------- 1 ファイルの読み取り

        private static void ProcessFile(
            LogFileEntry file, AggregatorOptions o, string[] extensions, HashSet<string> methods,
            HashSet<string> internalExact, List<uint[]> internalCidrs, string[] botNeedles,
            List<Regex> uriRegexes, HashSet<string> targetDates, string[] clientIpFields,
            ConcurrentDictionary<string, bool> internalVerdict,
            Dictionary<ReqKey, ReqBucket> req, Dictionary<int, Dictionary<string, long>> variants,
            List<EventRec> events, Counters c)
        {
            // フィールド位置。#Fields: 行が現れるたびに更新する (ログ途中で書式が変わることがある)。
            int iDate = -1, iTime = -1, iMethod = -1, iStem = -1, iUser = -1, iStatus = -1, iUa = -1, iTaken = -1, iBytes = -1;
            int[] iClientIp = new int[clientIpFields.Length];
            for (int k = 0; k < iClientIp.Length; k++) iClientIp[k] = -1;
            int maxIndex = -1;

            var dateCache = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var localDateCache = new Dictionary<long, int>();   // (日付の通日 * 4 + 日ずれ) -> 日付 ID
            var fields = new string[64];
            int serverId = GetId(_serverIds, file.ServerName, ref _nextServer);

            using (var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true, 1 << 16))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (line[0] == '#')
                    {
                        if (line.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
                        {
                            var names = line.Substring(8).Trim().Split(' ');
                            iDate = IndexOf(names, "date"); iTime = IndexOf(names, "time");
                            iMethod = IndexOf(names, "cs-method"); iStem = IndexOf(names, "cs-uri-stem");
                            iUser = IndexOf(names, "cs-username"); iStatus = IndexOf(names, "sc-status");
                            iUa = IndexOf(names, "cs(User-Agent)"); iTaken = IndexOf(names, "time-taken");
                            iBytes = IndexOf(names, "sc-bytes");
                            maxIndex = Max(iDate, iTime, iMethod, iStem, iUser, iStatus, iUa, iTaken, iBytes);
                            for (int k = 0; k < clientIpFields.Length; k++)
                            {
                                iClientIp[k] = IndexOf(names, clientIpFields[k]);
                                if (iClientIp[k] > maxIndex) maxIndex = iClientIp[k];
                            }
                            if (fields.Length <= maxIndex) fields = new string[maxIndex + 1];
                        }
                        continue;
                    }

                    c.Read++;
                    if (iStem < 0) { c.Malformed++; continue; }

                    // 最速の足切り: 対象拡張子を含まない行はここで捨てる。
                    if (extensions.Length > 0 && !ContainsAny(line, extensions)) { c.SkipExt++; continue; }

                    int count = SplitInto(line, fields, maxIndex);
                    if (count <= maxIndex) { c.Malformed++; continue; }

                    string url = fields[iStem];
                    int q = url.IndexOf('?');
                    if (q >= 0) url = url.Substring(0, q);          // GET パラメータは落とす
                    if (!EndsWithAny(url, extensions)) { c.SkipExt++; continue; }

                    string method = iMethod >= 0 ? fields[iMethod] : "-";
                    if (methods.Count > 0 && !methods.Contains(method)) { c.SkipFilter++; continue; }

                    if (uriRegexes.Count > 0)
                    {
                        bool drop = false;
                        for (int k = 0; k < uriRegexes.Count; k++) { if (uriRegexes[k].IsMatch(url)) { drop = true; break; } }
                        if (drop) { c.SkipFilter++; continue; }
                    }

                    int localDateId, timeSec; bool inRange;
                    if (!ResolveLocalTime(fields, iDate, iTime, o.TimeZoneOffsetMinutes, targetDates,
                                          dateCache, localDateCache, out localDateId, out timeSec, out inRange))
                    { c.Malformed++; continue; }
                    if (!inRange) { c.SkipRange++; continue; }

                    int status = 0;
                    if (iStatus >= 0) int.TryParse(fields[iStatus], NumberStyles.Integer, CultureInfo.InvariantCulture, out status);
                    int taken = 0;
                    if (iTaken >= 0) int.TryParse(fields[iTaken], NumberStyles.Integer, CultureInfo.InvariantCulture, out taken);
                    int bytes = 0;
                    if (iBytes >= 0) int.TryParse(fields[iBytes], NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);

                    // クライアント IP: 設定順に最初に見つかった有効な値を使う (XFF -> c-ip など)。
                    string ip = null;
                    for (int k = 0; k < iClientIp.Length; k++)
                    {
                        int idx = iClientIp[k];
                        if (idx < 0) continue;
                        string v = fields[idx];
                        if (v.Length == 0 || v == "-") continue;
                        int comma = v.IndexOf(',');                  // XFF は "client, proxy1, proxy2"
                        if (comma >= 0) v = v.Substring(0, comma);
                        v = v.Replace("+", " ").Trim();              // IIS は空白を + にエスケープする
                        if (v.Length == 0 || v == "-") continue;
                        ip = v; break;
                    }
                    if (ip == null) ip = "unknown";

                    bool isInternal;
                    if (!internalVerdict.TryGetValue(ip, out isInternal))
                    {
                        isInternal = IsIpMatch(ip, internalExact, internalCidrs);
                        internalVerdict.TryAdd(ip, isInternal);
                    }
                    bool isBot = botNeedles.Length > 0 && iUa >= 0 && ContainsAny(fields[iUa], botNeedles);

                    if (o.StripTrailingSlash && url.Length > 1 && url[url.Length - 1] == '/')
                        url = url.Substring(0, url.Length - 1);
                    // 小文字化は集計キーだけに効かせ、元の表記は display に残す。
                    string display = url;
                    if (o.LowercaseUrl) url = url.ToLowerInvariant();

                    int pageId = GetId(_pageIds, url, ref _nextPage);
                    if (o.LowercaseUrl && !string.Equals(display, url, StringComparison.Ordinal))
                    {
                        Dictionary<string, long> vmap;
                        if (!variants.TryGetValue(pageId, out vmap)) { vmap = new Dictionary<string, long>(StringComparer.Ordinal); variants[pageId] = vmap; }
                        long n; vmap.TryGetValue(display, out n); vmap[display] = n + 1;
                    }

                    // 認証ユーザーがいればそれを、いなければ IP を「人」とみなす。
                    string identity = null;
                    if (iUser >= 0)
                    {
                        string u = fields[iUser];
                        if (u.Length > 0 && u != "-") identity = "u:" + u;
                    }
                    int ipId = GetId(_ipIds, ip, ref _nextIp);
                    int userId = identity == null ? GetId(_userIds, "i:" + ip, ref _nextUser)
                                                  : GetId(_userIds, identity, ref _nextUser);

                    var key = new ReqKey();
                    key.Server = serverId; key.Date = localDateId; key.Page = pageId;
                    key.Method = GetId(_methodIds, method, ref _nextMethod);
                    key.StatusClass = (byte)(status >= 200 && status < 600 ? status / 100 : 0);
                    key.Flags = (byte)((isInternal ? 1 : 0) | (isBot ? 2 : 0));
                    ReqBucket bucket;
                    if (!req.TryGetValue(key, out bucket)) { bucket = new ReqBucket(); req[key] = bucket; }
                    bucket.Add(taken, bytes);

                    var ev = new EventRec();
                    ev.User = userId; ev.Ip = ipId; ev.Page = pageId; ev.Time = timeSec;
                    ev.Server = (short)serverId; ev.Date = (short)localDateId;
                    ev.Human = (byte)(!isInternal && !isBot ? 1 : 0);
                    events.Add(ev);

                    c.Counted++;
                }
            }
        }

        // ---------------------------------------------------------------- 訪問・人数

        /// <summary>
        /// 並べ替えは重いので 1 回だけ行い、全アクセス (all) と人のアクセスだけ (human) の
        /// 2 通りを同じ並びのまま数える。
        /// </summary>
        private static void SortAndScan(EventRec[] arr, bool byServer, int timeoutSec, RowSink sink)
        {
            if (arr.Length == 0) return;
            bool bs = byServer;
            Array.Sort(arr, delegate (EventRec a, EventRec b)
            {
                if (a.Date != b.Date) return a.Date < b.Date ? -1 : 1;
                if (bs && a.Server != b.Server) return a.Server < b.Server ? -1 : 1;
                if (a.User != b.User) return a.User < b.User ? -1 : 1;
                return a.Time < b.Time ? -1 : (a.Time > b.Time ? 1 : 0);
            });
            ScanGroups(arr, byServer, false, "all", timeoutSec, sink);
            ScanGroups(arr, byServer, true, "human", timeoutSec, sink);
        }

        /// <summary>
        /// 並べ替え済みのアクセスを (日 × サーバ × 利用者) ごとにまとめ、訪問・人数・入口出口を数える。
        /// byServer=false のときはサーバをまたいで 1 人として扱う (全サーバ合算の人数はサーバ別の合計にはならない)。
        /// </summary>
        private static void ScanGroups(EventRec[] arr, bool byServer, bool humanOnly, string segment,
                                       int timeoutSec, RowSink sink)
        {
            string dayScope = byServer ? "server-day" : "all-day";
            string pageScope = byServer ? "server-page" : "all-page";
            var pagesInVisit = new HashSet<int>();
            var pagesForUser = new HashSet<int>();

            int i = 0;
            while (i < arr.Length)
            {
                if (humanOnly && arr[i].Human == 0) { i++; continue; }
                int date = arr[i].Date;
                short server = byServer ? arr[i].Server : (short)(-1);
                int user = arr[i].User;

                // この利用者の 1 日分 (byServer ならサーバ内) を 1 グループとして処理する
                pagesForUser.Clear();
                long userViews = 0;
                int visitStartTime = arr[i].Time, prevTime = arr[i].Time;
                int visitViews = 0, entryPage = arr[i].Page, exitPage = arr[i].Page;
                pagesInVisit.Clear();

                var day = sink.Day(segment, dayScope, date, server);
                while (i < arr.Length)
                {
                    var e = arr[i];
                    if (e.Date != date || e.User != user || (byServer && e.Server != server)) break;
                    if (humanOnly && e.Human == 0) { i++; continue; }

                    if (visitViews > 0 && e.Time - prevTime > timeoutSec)
                    {
                        CloseVisit(sink, segment, pageScope, day, date, server,
                                   visitViews, pagesInVisit.Count, prevTime - visitStartTime, entryPage, exitPage);
                        pagesInVisit.Clear();
                        visitViews = 0; visitStartTime = e.Time; entryPage = e.Page;
                    }
                    if (visitViews == 0) { visitStartTime = e.Time; entryPage = e.Page; }

                    visitViews++;
                    pagesInVisit.Add(e.Page);
                    exitPage = e.Page;
                    prevTime = e.Time;

                    userViews++;
                    var pageAcc = sink.Page(segment, pageScope, date, server, e.Page);
                    if (pagesForUser.Add(e.Page)) pageAcc.Users++;   // 同じ利用者はページごとに 1 回だけ数える
                    pageAcc.Ips.Add(e.Ip);
                    day.Ips.Add(e.Ip);
                    i++;
                }

                if (visitViews > 0)
                {
                    CloseVisit(sink, segment, pageScope, day, date, server,
                               visitViews, pagesInVisit.Count, prevTime - visitStartTime, entryPage, exitPage);
                }

                if (userViews > 0)
                {
                    day.Users++;
                    day.PageViewsSum += userViews;
                    day.DistinctPagesPerUserSum += pagesForUser.Count;
                    if (pagesForUser.Count <= 1) day.Users1Page++;
                    else if (pagesForUser.Count <= 5) day.Users2to5++;
                    else day.Users6over++;
                }
            }
        }

        private static void CloseVisit(RowSink sink, string segment, string pageScope, DayAcc day,
                                       int date, short server, int views, int distinctPages, int durationSec,
                                       int entryPage, int exitPage)
        {
            day.Visits++;
            day.PageViewsInVisits += views;
            day.DistinctPagesInVisitsSum += distinctPages;
            day.DurationSecSum += durationSec;
            if (views == 1) day.SinglePageVisits++;

            if (views == 1) day.V1++;
            else if (views <= 3) day.V2to3++;
            else if (views <= 10) day.V4to10++;
            else day.V11over++;

            if (durationSec < 30) day.D0++;
            else if (durationSec < 180) day.D30++;
            else if (durationSec < 600) day.D180++;
            else if (durationSec < 1800) day.D600++;
            else day.DOver++;

            var entry = sink.Page(segment, pageScope, date, server, entryPage);
            entry.Entry++;
            if (views == 1) entry.Bounce++;
            sink.Page(segment, pageScope, date, server, exitPage).Exit++;
        }

        // ---------------------------------------------------------------- 行の組み立て

        private static RequestRow[] BuildRequestRows(Dictionary<ReqKey, ReqBucket> agg,
            string[] servers, string[] dates, string[] pages, string[] displays, string[] methods)
        {
            var rows = new RequestRow[agg.Count];
            int i = 0;
            foreach (var kv in agg)
            {
                var k = kv.Key; var b = kv.Value;
                var r = new RequestRow();
                r.ServerName = servers[k.Server]; r.LogDate = dates[k.Date];
                r.PageUrl = pages[k.Page]; r.PageUrlDisplay = displays[k.Page];
                r.Method = methods[k.Method];
                r.StatusClass = k.StatusClass == 0 ? "other" : k.StatusClass.ToString(CultureInfo.InvariantCulture) + "xx";
                r.IsInternal = (k.Flags & 1) != 0 ? 1 : 0;
                r.IsBot = (k.Flags & 2) != 0 ? 1 : 0;
                r.Requests = b.Requests; r.TimeTakenSumMs = b.TimeSum; r.TimeTakenMaxMs = b.TimeMax;
                r.BytesSentSum = b.Bytes;
                r.T100 = b.T100; r.T300 = b.T300; r.T1000 = b.T1000;
                r.T3000 = b.T3000; r.T10000 = b.T10000; r.TOver = b.TOver;
                rows[i++] = r;
            }
            Array.Sort(rows, delegate (RequestRow a, RequestRow b2)
            {
                int cmp = string.CompareOrdinal(a.LogDate, b2.LogDate);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.ServerName, b2.ServerName);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.PageUrl, b2.PageUrl);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.Method, b2.Method);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.StatusClass, b2.StatusClass);
            });
            return rows;
        }

        private static string[] BuildDisplayNames(string[] pages, Dictionary<int, Dictionary<string, long>> variants)
        {
            var displays = new string[pages.Length];
            for (int i = 0; i < pages.Length; i++) displays[i] = pages[i];
            foreach (var kv in variants)
            {
                string best = null; long bestN = -1;
                foreach (var v in kv.Value)
                {
                    // 同数なら表記を固定するため序列で決める (日によって揺れないように)
                    if (v.Value > bestN || (v.Value == bestN && string.CompareOrdinal(v.Key, best) < 0))
                    { best = v.Key; bestN = v.Value; }
                }
                if (best != null) displays[kv.Key] = best;
            }
            return displays;
        }

        // ---- 集計の受け皿 ----

        internal class DayAcc
        {
            public long Visits, PageViewsInVisits, DistinctPagesInVisitsSum, DurationSecSum, SinglePageVisits;
            public long V1, V2to3, V4to10, V11over;
            public long D0, D30, D180, D600, DOver;
            public long Users, PageViewsSum, DistinctPagesPerUserSum;
            public long Users1Page, Users2to5, Users6over;
            public HashSet<int> Ips = new HashSet<int>();
        }

        internal class PageAcc
        {
            public long Users, Entry, Exit, Bounce;
            public HashSet<int> Ips = new HashSet<int>();
        }

        internal struct DayKey : IEquatable<DayKey>
        {
            public string Segment, Scope;
            public int Date; public short Server;
            public bool Equals(DayKey o)
            {
                return Date == o.Date && Server == o.Server
                    && string.Equals(Segment, o.Segment, StringComparison.Ordinal)
                    && string.Equals(Scope, o.Scope, StringComparison.Ordinal);
            }
            public override bool Equals(object o) { return o is DayKey && Equals((DayKey)o); }
            public override int GetHashCode() { return ((Date * 397) ^ Server) * 397 ^ (Segment.Length * 31 + Scope.Length); }
        }

        internal struct PageKey : IEquatable<PageKey>
        {
            public string Segment, Scope;
            public int Date, Page; public short Server;
            public bool Equals(PageKey o)
            {
                return Date == o.Date && Page == o.Page && Server == o.Server
                    && string.Equals(Segment, o.Segment, StringComparison.Ordinal)
                    && string.Equals(Scope, o.Scope, StringComparison.Ordinal);
            }
            public override bool Equals(object o) { return o is PageKey && Equals((PageKey)o); }
            public override int GetHashCode()
            {
                int h = Date; h = h * 397 ^ Page; h = h * 397 ^ Server;
                return h * 397 ^ (Segment.Length * 31 + Scope.Length);
            }
        }

        internal class RowSink
        {
            private readonly Dictionary<DayKey, DayAcc> _days = new Dictionary<DayKey, DayAcc>();
            private readonly Dictionary<PageKey, PageAcc> _pages = new Dictionary<PageKey, PageAcc>();

            public DayAcc Day(string segment, string scope, int date, short server)
            {
                var k = new DayKey(); k.Segment = segment; k.Scope = scope; k.Date = date; k.Server = server;
                DayAcc a;
                if (!_days.TryGetValue(k, out a)) { a = new DayAcc(); _days[k] = a; }
                return a;
            }

            public PageAcc Page(string segment, string scope, int date, short server, int page)
            {
                var k = new PageKey(); k.Segment = segment; k.Scope = scope; k.Date = date; k.Server = server; k.Page = page;
                PageAcc a;
                if (!_pages.TryGetValue(k, out a)) { a = new PageAcc(); _pages[k] = a; }
                return a;
            }

            public void Emit(AggregateResult result, string[] servers, string[] dates, string[] pages, string[] displays)
            {
                var uniques = new List<UniqueRow>();
                var visits = new List<VisitRow>();
                var flows = new List<FlowRow>();
                var users = new List<UserRow>();

                foreach (var kv in _days)
                {
                    var k = kv.Key; var a = kv.Value;
                    string server = k.Server >= 0 ? servers[k.Server] : "";
                    string date = dates[k.Date];

                    var u = new UniqueRow();
                    u.Scope = k.Scope; u.Segment = k.Segment; u.ServerName = server; u.LogDate = date;
                    u.PageUrl = ""; u.PageUrlDisplay = "";
                    u.UniqueUsers = (int)a.Users; u.UniqueIps = a.Ips.Count;
                    uniques.Add(u);

                    var v = new VisitRow();
                    v.Scope = k.Scope; v.Segment = k.Segment; v.ServerName = server; v.LogDate = date;
                    v.Visits = a.Visits; v.PageViewsInVisits = a.PageViewsInVisits;
                    v.DistinctPagesInVisitsSum = a.DistinctPagesInVisitsSum;
                    v.DurationSecSum = a.DurationSecSum; v.SinglePageVisits = a.SinglePageVisits;
                    v.V1 = a.V1; v.V2to3 = a.V2to3; v.V4to10 = a.V4to10; v.V11over = a.V11over;
                    v.D0 = a.D0; v.D30 = a.D30; v.D180 = a.D180; v.D600 = a.D600; v.DOver = a.DOver;
                    visits.Add(v);

                    var ur = new UserRow();
                    ur.Scope = k.Scope; ur.Segment = k.Segment; ur.ServerName = server; ur.LogDate = date;
                    ur.Users = a.Users; ur.PageViewsSum = a.PageViewsSum;
                    ur.DistinctPagesPerUserSum = a.DistinctPagesPerUserSum;
                    ur.Users1Page = a.Users1Page; ur.Users2to5 = a.Users2to5; ur.Users6over = a.Users6over;
                    users.Add(ur);
                }

                foreach (var kv in _pages)
                {
                    var k = kv.Key; var a = kv.Value;
                    string server = k.Server >= 0 ? servers[k.Server] : "";
                    string date = dates[k.Date];

                    var u = new UniqueRow();
                    u.Scope = k.Scope; u.Segment = k.Segment; u.ServerName = server; u.LogDate = date;
                    u.PageUrl = pages[k.Page]; u.PageUrlDisplay = displays[k.Page];
                    u.UniqueUsers = (int)a.Users; u.UniqueIps = a.Ips.Count;
                    uniques.Add(u);

                    if (a.Entry > 0 || a.Exit > 0 || a.Bounce > 0)
                    {
                        var f = new FlowRow();
                        f.Scope = k.Scope; f.Segment = k.Segment; f.ServerName = server; f.LogDate = date;
                        f.PageUrl = pages[k.Page]; f.PageUrlDisplay = displays[k.Page];
                        f.EntryCount = a.Entry; f.ExitCount = a.Exit; f.BounceCount = a.Bounce;
                        flows.Add(f);
                    }
                }

                uniques.Sort(delegate (UniqueRow a, UniqueRow b)
                {
                    int cmp = string.CompareOrdinal(a.LogDate, b.LogDate); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Segment, b.Segment); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Scope, b.Scope); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.ServerName, b.ServerName); if (cmp != 0) return cmp;
                    return string.CompareOrdinal(a.PageUrl, b.PageUrl);
                });
                visits.Sort(delegate (VisitRow a, VisitRow b)
                {
                    int cmp = string.CompareOrdinal(a.LogDate, b.LogDate); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Segment, b.Segment); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Scope, b.Scope); if (cmp != 0) return cmp;
                    return string.CompareOrdinal(a.ServerName, b.ServerName);
                });
                flows.Sort(delegate (FlowRow a, FlowRow b)
                {
                    int cmp = string.CompareOrdinal(a.LogDate, b.LogDate); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Segment, b.Segment); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Scope, b.Scope); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.ServerName, b.ServerName); if (cmp != 0) return cmp;
                    return string.CompareOrdinal(a.PageUrl, b.PageUrl);
                });
                users.Sort(delegate (UserRow a, UserRow b)
                {
                    int cmp = string.CompareOrdinal(a.LogDate, b.LogDate); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Segment, b.Segment); if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Scope, b.Scope); if (cmp != 0) return cmp;
                    return string.CompareOrdinal(a.ServerName, b.ServerName);
                });

                result.Uniques = uniques.ToArray();
                result.Visits = visits.ToArray();
                result.Flows = flows.ToArray();
                result.Users = users.ToArray();
            }
        }

        // ---- 小物 ----

        private static int SplitInto(string line, string[] fields, int maxIndex)
        {
            int n = 0, start = 0, len = line.Length;
            for (int i = 0; i <= len; i++)
            {
                if (i == len || line[i] == ' ')
                {
                    if (n < fields.Length) fields[n] = line.Substring(start, i - start);
                    n++;
                    start = i + 1;
                    if (n > maxIndex) return n;
                }
            }
            return n;
        }

        /// <summary>
        /// ログの日時をローカル日付 ID と秒 (2000-01-01 起点) に変換する。
        /// 対象日かどうかもここで判定する (文字列比較を日付ごとに 1 回で済ませるため)。
        /// </summary>
        private static bool ResolveLocalTime(string[] fields, int iDate, int iTime, int offsetMinutes,
                                             HashSet<string> targetDates,
                                             Dictionary<string, DateTime> dateCache,
                                             Dictionary<long, int> localDateCache,
                                             out int localDateId, out int timeSec, out bool inRange)
        {
            localDateId = -1; timeSec = 0; inRange = false;
            if (iDate < 0) return false;
            string d = fields[iDate];
            if (d.Length < 10) return false;

            DateTime day;
            if (!dateCache.TryGetValue(d, out day))
            {
                if (!DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                    return false;
                dateCache[d] = day;
            }

            int hh = 0, mm = 0, ss = 0;
            if (iTime >= 0)
            {
                string t = fields[iTime];
                if (t.Length >= 8)
                {
                    int.TryParse(t.Substring(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out hh);
                    int.TryParse(t.Substring(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out mm);
                    int.TryParse(t.Substring(6, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out ss);
                }
            }

            long dayNumber = day.Ticks / TimeSpan.TicksPerDay;
            long epochDay = Epoch.Ticks / TimeSpan.TicksPerDay;
            int secOfDay = hh * 3600 + mm * 60 + ss + offsetMinutes * 60;
            timeSec = (int)((dayNumber - epochDay) * 86400L + secOfDay);

            // 時差補正で日をまたぐことがある (UTC 15:00 以降は日本時間では翌日)
            int dayShift = (int)Math.Floor(secOfDay / 86400.0);
            long cacheKey = (dayNumber << 2) + (dayShift + 1);
            int cached;
            if (localDateCache.TryGetValue(cacheKey, out cached))
            {
                localDateId = cached >> 1;
                inRange = (cached & 1) != 0;
                return true;
            }
            string local = day.AddDays(dayShift).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            localDateId = GetId(_dateIds, local, ref _nextDate);
            inRange = targetDates.Count == 0 || targetDates.Contains(local);
            localDateCache[cacheKey] = (localDateId << 1) | (inRange ? 1 : 0);
            return true;
        }

        private static int IndexOf(string[] names, string name)
        {
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int Max(params int[] values)
        {
            int m = -1;
            foreach (var v in values) if (v > m) m = v;
            return m;
        }

        private static bool ContainsAny(string s, string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
                if (s.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool EndsWithAny(string s, string[] suffixes)
        {
            if (suffixes.Length == 0) return true;
            for (int i = 0; i < suffixes.Length; i++)
                if (s.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static List<uint[]> ParseCidrs(string[] cidrs)
        {
            var list = new List<uint[]>();
            foreach (var c in (cidrs ?? new string[0]))
            {
                if (string.IsNullOrEmpty(c)) continue;
                var parts = c.Split('/');
                uint baseIp;
                if (!TryIpv4ToUInt(parts[0].Trim(), out baseIp))
                    throw new ArgumentException("InternalIpRanges の値が IPv4 CIDR として解釈できません: " + c);
                int bits = 32;
                if (parts.Length > 1 && !int.TryParse(parts[1].Trim(), out bits))
                    throw new ArgumentException("InternalIpRanges のプレフィックス長が不正です: " + c);
                if (bits < 0 || bits > 32)
                    throw new ArgumentException("InternalIpRanges のプレフィックス長が不正です: " + c);
                uint mask = bits == 0 ? 0u : (0xFFFFFFFFu << (32 - bits));
                list.Add(new uint[] { baseIp & mask, mask });
            }
            return list;
        }

        private static bool TryIpv4ToUInt(string ip, out uint value)
        {
            value = 0;
            System.Net.IPAddress addr;
            if (!System.Net.IPAddress.TryParse(ip, out addr)) return false;
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            var b = addr.GetAddressBytes();
            value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return true;
        }

        private static bool IsIpMatch(string ip, HashSet<string> exact, List<uint[]> cidrs)
        {
            if (exact.Contains(ip)) return true;
            if (cidrs.Count == 0) return false;
            uint v;
            if (!TryIpv4ToUInt(ip, out v)) return false;   // IPv6 は完全一致のみ
            for (int i = 0; i < cidrs.Count; i++)
                if ((v & cidrs[i][1]) == cidrs[i][0]) return true;
            return false;
        }
    }
}
