// IIS W3C ログの高速集計エンジン。
// PowerShell 側から Add-Type で読み込んで使う (C# 5 / .NET Framework 4.5 互換で記述)。
//
// 速度のための設計:
//   * ファイル単位で Parallel.ForEach。各スレッドはローカル辞書に集計し、最後に一度だけマージする。
//   * 1 行につき文字列 Split を行わず、必要なフィールドだけを 1 パスで切り出す。
//   * 最初に「対象拡張子を含まない行」を IndexOf で捨てる。IIS ログの大半は静的ファイルなので、
//     ここで落ちる行はこれ以降の処理を一切行わない (実測でこれが最も効く)。
//   * ユニーク数は文字列の HashSet ではなく、IP/識別子を int の ID に採番して HashSet<int> で保持する。
//     ページ数 × ユニーク数のメモリを数分の 1 に抑えられる。
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
        public string[] IncludeMethods = new string[0];      // 空なら全メソッド
        public int[] ExcludeStatusCodes = new int[0];
        public string[] ExcludeIpExact = new string[0];
        public string[] ExcludeIpCidr = new string[0];       // IPv4 のみ (例 10.0.0.0/8)
        public string[] ExcludeUserAgentContains = new string[0];
        public string[] ExcludeUriPatterns = new string[0];  // 正規表現
        public string[] ClientIpFields = new string[] { "c-ip" };
        public bool LowercaseUrl = true;
        public bool StripTrailingSlash = false;
        public int TimeZoneOffsetMinutes = 0;
        public string[] TargetDates = new string[0];         // "yyyy-MM-dd" (ローカル日付)。空なら全日付
        public int MaxDegreeOfParallelism = 0;               // 0 なら CPU 数
    }

    /// <summary>ページ単位の 1 行。</summary>
    public class PageRow
    {
        public string ServerName;
        public string LogDate;
        public string PageUrl;         // 名寄せ後のキー (LowercaseUrl が true なら小文字)
        public string PageUrlDisplay;  // 表示用。ログに最も多く現れた元の表記
        public long PageViews;
        public int UniqueUsers;
        public int UniqueIps;
        public double AvgTimeTakenMs;
        public int MaxTimeTakenMs;
        public long ErrorCount;
    }

    /// <summary>サーバー × 日単位の 1 行。ユニーク数はページ横断では足し算できないのでここで別途持つ。</summary>
    public class DailyRow
    {
        public string ServerName;
        public string LogDate;
        public long PageViews;
        public int UniqueUsers;
        public int UniqueIps;
        public double AvgTimeTakenMs;
        public long ErrorCount;
        public int PageCount;
    }

    public class AggregateResult
    {
        public PageRow[] Pages = new PageRow[0];
        public DailyRow[] Daily = new DailyRow[0];
        public long LinesRead;
        public long LinesCounted;
        public long LinesSkippedExtension;
        public long LinesSkippedExcludedIp;
        public long LinesSkippedFilter;
        public long LinesSkippedOutOfRange;
        public long LinesMalformed;
        public int FilesRead;
        public string[] Errors = new string[0];
        public double ElapsedSeconds;
    }

    internal class Bucket
    {
        public long Hits;
        public long TimeSum;
        public int TimeMax;
        public long Errors;
        public HashSet<int> Users = new HashSet<int>();
        public HashSet<int> Ips = new HashSet<int>();
        // 元の URL 表記ごとの出現回数。小文字で名寄せしつつ、表示用に一番多かった
        // 表記を選ぶために持つ (ページ集計だけで使い、日次集計では null のまま)。
        public Dictionary<string, long> Variants;
    }

    internal class Counters
    {
        public long Read, Counted, SkipExt, SkipIp, SkipFilter, SkipRange, Malformed;
    }

    public static class IisLogAggregator
    {
        // IP / 識別子の文字列 -> int ID。全スレッド共有。
        // ここを共有 ID にすることで、各バケットが持つのは int の集合だけになる。
        private static ConcurrentDictionary<string, int> _idMap;
        private static int _nextId;

        private static int GetId(string value)
        {
            int id;
            if (_idMap.TryGetValue(value, out id)) return id;
            return _idMap.GetOrAdd(value, System.Threading.Interlocked.Increment(ref _nextId));
        }

        public static AggregateResult Run(LogFileEntry[] files, AggregatorOptions o)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _idMap = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            _nextId = 0;

            var extensions = o.TargetExtensions == null ? new string[0] : o.TargetExtensions;
            var methods = new HashSet<string>(o.IncludeMethods ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var badStatus = new HashSet<int>(o.ExcludeStatusCodes ?? new int[0]);
            var ipExact = new HashSet<string>(o.ExcludeIpExact ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var cidrs = ParseCidrs(o.ExcludeIpCidr);
            var uaNeedles = o.ExcludeUserAgentContains ?? new string[0];
            var uriRegexes = new List<Regex>();
            foreach (var p in (o.ExcludeUriPatterns ?? new string[0]))
                uriRegexes.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant));
            var targetDates = new HashSet<string>(o.TargetDates ?? new string[0], StringComparer.Ordinal);
            var clientIpFields = (o.ClientIpFields == null || o.ClientIpFields.Length == 0)
                ? new string[] { "c-ip" } : o.ClientIpFields;

            // 除外 IP 判定の結果キャッシュ。同じ IP が何万回も出るので効く。
            var ipVerdict = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

            var merged = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            var mergedDaily = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            var total = new Counters();
            var errors = new ConcurrentBag<string>();
            int filesRead = 0;
            object gate = new object();

            var po = new ParallelOptions();
            po.MaxDegreeOfParallelism = o.MaxDegreeOfParallelism > 0
                ? o.MaxDegreeOfParallelism : Environment.ProcessorCount;

            Parallel.ForEach(files, po, file =>
            {
                var local = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                var localDaily = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                var c = new Counters();
                try
                {
                    ProcessFile(file, o, extensions, methods, badStatus, ipExact, cidrs, uaNeedles,
                                uriRegexes, targetDates, clientIpFields, ipVerdict, local, localDaily, c);
                    System.Threading.Interlocked.Increment(ref filesRead);
                }
                catch (Exception ex)
                {
                    errors.Add(file.Path + ": " + ex.Message);
                }

                lock (gate)
                {
                    MergeInto(merged, local);
                    MergeInto(mergedDaily, localDaily);
                    total.Read += c.Read; total.Counted += c.Counted; total.SkipExt += c.SkipExt;
                    total.SkipIp += c.SkipIp; total.SkipFilter += c.SkipFilter;
                    total.SkipRange += c.SkipRange; total.Malformed += c.Malformed;
                }
            });

            var result = new AggregateResult();
            result.Pages = BuildPageRows(merged);
            result.Daily = BuildDailyRows(mergedDaily, result.Pages);
            result.LinesRead = total.Read;
            result.LinesCounted = total.Counted;
            result.LinesSkippedExtension = total.SkipExt;
            result.LinesSkippedExcludedIp = total.SkipIp;
            result.LinesSkippedFilter = total.SkipFilter;
            result.LinesSkippedOutOfRange = total.SkipRange;
            result.LinesMalformed = total.Malformed;
            result.FilesRead = filesRead;
            result.Errors = errors.ToArray();
            _idMap = null; // ID テーブルは大きくなるので参照を切って GC に返す
            sw.Stop();
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        private static void MergeInto(Dictionary<string, Bucket> dst, Dictionary<string, Bucket> src)
        {
            foreach (var kv in src)
            {
                Bucket b;
                if (!dst.TryGetValue(kv.Key, out b)) { dst[kv.Key] = kv.Value; continue; }
                b.Hits += kv.Value.Hits;
                b.TimeSum += kv.Value.TimeSum;
                b.Errors += kv.Value.Errors;
                if (kv.Value.TimeMax > b.TimeMax) b.TimeMax = kv.Value.TimeMax;
                b.Users.UnionWith(kv.Value.Users);
                b.Ips.UnionWith(kv.Value.Ips);
                if (kv.Value.Variants != null)
                {
                    if (b.Variants == null) { b.Variants = kv.Value.Variants; }
                    else
                    {
                        foreach (var v in kv.Value.Variants)
                        {
                            long n;
                            b.Variants.TryGetValue(v.Key, out n);
                            b.Variants[v.Key] = n + v.Value;
                        }
                    }
                }
            }
        }

        private static void ProcessFile(
            LogFileEntry file, AggregatorOptions o, string[] extensions, HashSet<string> methods,
            HashSet<int> badStatus, HashSet<string> ipExact, List<uint[]> cidrs, string[] uaNeedles,
            List<Regex> uriRegexes, HashSet<string> targetDates, string[] clientIpFields,
            ConcurrentDictionary<string, bool> ipVerdict,
            Dictionary<string, Bucket> agg, Dictionary<string, Bucket> aggDaily, Counters c)
        {
            // フィールド位置。#Fields: 行が現れるたびに更新する (ログ途中で書式が変わることがある)。
            int iDate = -1, iTime = -1, iMethod = -1, iStem = -1, iUser = -1, iStatus = -1, iUa = -1, iTaken = -1;
            int[] iClientIp = new int[clientIpFields.Length];
            for (int k = 0; k < iClientIp.Length; k++) iClientIp[k] = -1;
            int maxIndex = -1;

            // 「その日の何時何分がローカル日付で何日か」のキャッシュ。ファイルごとにローカルに持つ。
            var dateCache = new Dictionary<string, string>(StringComparer.Ordinal);
            var fields = new string[64];
            string serverName = file.ServerName;

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
                            maxIndex = Max(iDate, iTime, iMethod, iStem, iUser, iStatus, iUa, iTaken);
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

                    if (methods.Count > 0 && iMethod >= 0 && !methods.Contains(fields[iMethod])) { c.SkipFilter++; continue; }

                    int status = 0;
                    if (iStatus >= 0) int.TryParse(fields[iStatus], NumberStyles.Integer, CultureInfo.InvariantCulture, out status);
                    if (badStatus.Count > 0 && badStatus.Contains(status)) { c.SkipFilter++; continue; }

                    if (uaNeedles.Length > 0 && iUa >= 0 && ContainsAny(fields[iUa], uaNeedles)) { c.SkipFilter++; continue; }

                    if (uriRegexes.Count > 0)
                    {
                        bool drop = false;
                        for (int k = 0; k < uriRegexes.Count; k++) { if (uriRegexes[k].IsMatch(url)) { drop = true; break; } }
                        if (drop) { c.SkipFilter++; continue; }
                    }

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

                    bool excluded;
                    if (!ipVerdict.TryGetValue(ip, out excluded))
                    {
                        excluded = IsIpExcluded(ip, ipExact, cidrs);
                        ipVerdict.TryAdd(ip, excluded);
                    }
                    if (excluded) { c.SkipIp++; continue; }

                    string logDate = ResolveLocalDate(fields, iDate, iTime, o.TimeZoneOffsetMinutes, dateCache);
                    if (logDate == null) { c.Malformed++; continue; }
                    if (targetDates.Count > 0 && !targetDates.Contains(logDate)) { c.SkipRange++; continue; }

                    if (o.StripTrailingSlash && url.Length > 1 && url[url.Length - 1] == '/')
                        url = url.Substring(0, url.Length - 1);
                    // 小文字化は集計キーだけに効かせ、元の表記は display に残す。
                    string display = url;
                    if (o.LowercaseUrl) url = url.ToLowerInvariant();

                    // 認証ユーザーがいればそれを、いなければ IP を「人」とみなす。
                    string identity = null;
                    if (iUser >= 0)
                    {
                        string u = fields[iUser];
                        if (u.Length > 0 && u != "-") identity = "u:" + u;
                    }

                    int taken = 0;
                    if (iTaken >= 0) int.TryParse(fields[iTaken], NumberStyles.Integer, CultureInfo.InvariantCulture, out taken);

                    int ipId = GetId(ip);
                    int userId = identity == null ? ipId : GetId(identity);

                    string key = serverName + "\t" + logDate + "\t" + url;
                    Accumulate(agg, key, taken, status, ipId, userId, o.LowercaseUrl ? display : null);
                    Accumulate(aggDaily, serverName + "\t" + logDate, taken, status, ipId, userId, null);
                    c.Counted++;
                }
            }
        }

        private static void Accumulate(Dictionary<string, Bucket> agg, string key, int taken, int status,
                                       int ipId, int userId, string variant)
        {
            Bucket b;
            if (!agg.TryGetValue(key, out b)) { b = new Bucket(); agg[key] = b; }
            b.Hits++;
            b.TimeSum += taken;
            if (taken > b.TimeMax) b.TimeMax = taken;
            if (status >= 400) b.Errors++;
            b.Ips.Add(ipId);
            b.Users.Add(userId);
            if (variant != null)
            {
                if (b.Variants == null) b.Variants = new Dictionary<string, long>(StringComparer.Ordinal);
                long n;
                b.Variants.TryGetValue(variant, out n);
                b.Variants[variant] = n + 1;
            }
        }

        /// <summary>元表記のうち一番多かったものを返す。同数なら順序で決めて実行ごとにぶれないようにする。</summary>
        private static string PickDisplayUrl(Dictionary<string, long> variants, string fallback)
        {
            if (variants == null || variants.Count == 0) return fallback;
            string best = null;
            long bestCount = -1;
            foreach (var kv in variants)
            {
                if (kv.Value > bestCount || (kv.Value == bestCount && string.CompareOrdinal(kv.Key, best) < 0))
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            return best;
        }

        /// <summary>maxIndex までのフィールドだけを空白区切りで切り出す。全体 Split よりアロケーションが少ない。</summary>
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

        private static string ResolveLocalDate(string[] fields, int iDate, int iTime, int offsetMinutes,
                                               Dictionary<string, string> cache)
        {
            if (iDate < 0) return null;
            string d = fields[iDate];
            if (d.Length < 10) return null;
            if (offsetMinutes == 0) return d;

            string t = iTime >= 0 ? fields[iTime] : "00:00:00";
            string key = t.Length >= 5 ? d + t.Substring(0, 5) : d;
            string cached;
            if (cache.TryGetValue(key, out cached)) return cached;

            DateTime parsed;
            if (!DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return null;
            int hh = 0, mm = 0;
            if (t.Length >= 5)
            {
                int.TryParse(t.Substring(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out hh);
                int.TryParse(t.Substring(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out mm);
            }
            string local = parsed.AddMinutes(hh * 60 + mm + offsetMinutes).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            cache[key] = local;
            return local;
        }

        private static PageRow[] BuildPageRows(Dictionary<string, Bucket> agg)
        {
            var rows = new PageRow[agg.Count];
            int i = 0;
            foreach (var kv in agg)
            {
                var parts = kv.Key.Split('\t');
                var b = kv.Value;
                var r = new PageRow();
                r.ServerName = parts[0]; r.LogDate = parts[1]; r.PageUrl = parts[2];
                r.PageUrlDisplay = PickDisplayUrl(b.Variants, r.PageUrl);
                r.PageViews = b.Hits; r.UniqueUsers = b.Users.Count; r.UniqueIps = b.Ips.Count;
                r.AvgTimeTakenMs = b.Hits > 0 ? Math.Round((double)b.TimeSum / b.Hits, 1) : 0;
                r.MaxTimeTakenMs = b.TimeMax; r.ErrorCount = b.Errors;
                rows[i++] = r;
            }
            Array.Sort(rows, delegate (PageRow a, PageRow b2)
            {
                int cmp = string.CompareOrdinal(a.LogDate, b2.LogDate);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.ServerName, b2.ServerName);
                if (cmp != 0) return cmp;
                cmp = b2.PageViews.CompareTo(a.PageViews);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.PageUrl, b2.PageUrl);
            });
            return rows;
        }

        private static DailyRow[] BuildDailyRows(Dictionary<string, Bucket> agg, PageRow[] pages)
        {
            var pageCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in pages)
            {
                string k = p.ServerName + "\t" + p.LogDate;
                int n;
                pageCount.TryGetValue(k, out n);
                pageCount[k] = n + 1;
            }
            var rows = new DailyRow[agg.Count];
            int i = 0;
            foreach (var kv in agg)
            {
                var parts = kv.Key.Split('\t');
                var b = kv.Value;
                var r = new DailyRow();
                r.ServerName = parts[0]; r.LogDate = parts[1];
                r.PageViews = b.Hits; r.UniqueUsers = b.Users.Count; r.UniqueIps = b.Ips.Count;
                r.AvgTimeTakenMs = b.Hits > 0 ? Math.Round((double)b.TimeSum / b.Hits, 1) : 0;
                r.ErrorCount = b.Errors;
                int pc; pageCount.TryGetValue(kv.Key, out pc); r.PageCount = pc;
                rows[i++] = r;
            }
            Array.Sort(rows, delegate (DailyRow a, DailyRow b2)
            {
                int cmp = string.CompareOrdinal(a.LogDate, b2.LogDate);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.ServerName, b2.ServerName);
            });
            return rows;
        }

        // ---- 小物 ----

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
                    throw new ArgumentException("ExcludeIpRanges の値が IPv4 CIDR として解釈できません: " + c);
                int bits = 32;
                if (parts.Length > 1 && !int.TryParse(parts[1].Trim(), out bits))
                    throw new ArgumentException("ExcludeIpRanges のプレフィックス長が不正です: " + c);
                if (bits < 0 || bits > 32)
                    throw new ArgumentException("ExcludeIpRanges のプレフィックス長が不正です: " + c);
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

        private static bool IsIpExcluded(string ip, HashSet<string> exact, List<uint[]> cidrs)
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
