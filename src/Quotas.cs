using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AnbyPet
{
    internal sealed class RunningProviders
    {
        internal bool Codex;
        internal string CodexExecutable;
        internal bool Antigravity;
        internal bool AntigravityCli;
    }

    internal static class ProviderProcesses
    {
        internal static RunningProviders Read(int ignoreAgyPid = 0, int ignoreCodexPid = 0)
        {
            var running = new RunningProviders();
            int ownSession;
            using (var own = Process.GetCurrentProcess()) ownSession = own.SessionId;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var name = process.ProcessName.ToLowerInvariant();
                        switch (name)
                        {
                            case "codex":
                                if (process.Id != ignoreCodexPid && process.SessionId == ownSession)
                                {
                                    running.Codex = true;
                                    if (string.IsNullOrEmpty(running.CodexExecutable))
                                        running.CodexExecutable = ImagePath(process.Id);
                                }
                                break;
                            case "chatgpt":
                                if (!running.Codex && process.SessionId == ownSession)
                                {
                                    var image = ImagePath(process.Id);
                                    running.Codex = image.IndexOf(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
                                }
                                break;
                            case "antigravity":
                            case "antigravity ide":
                            case "antigravity-ide":
                                running.Antigravity |= process.SessionId == ownSession;
                                break;
                            case "agy":
                            case "antigravity-cli":
                                if (ignoreAgyPid == 0 || process.Id != ignoreAgyPid)
                                    running.AntigravityCli |= process.SessionId == ownSession;
                                break;
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            running.Antigravity |= running.AntigravityCli;
            return running;
        }

        private static string ImagePath(int pid)
        {
            var handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) return "";
            try
            {
                var path = new StringBuilder(1024);
                int size = path.Capacity;
                return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : "";
            }
            finally { CloseHandle(handle); }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder image, ref int size);
    }

    internal sealed class QuotaWindow
    {
        internal string Name;
        internal double? RemainingPercent;
        internal double? RemainingAmount;
        internal DateTimeOffset? Reset;
    }

    internal sealed class QuotaSnapshot
    {
        internal DateTimeOffset Captured;
        internal string Source;
        internal readonly List<QuotaWindow> Windows = new List<QuotaWindow>();
    }

    internal sealed class CodexSnapshots
    {
        private const int TailLimit = 2 * 1024 * 1024;
        internal static readonly string Profile = Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        private readonly string sessions = Path.Combine(Profile, "sessions");
        private readonly Dictionary<string, CachedFile> cache = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        private byte[] tailBuffer;

        internal QuotaSnapshot Read(CancellationToken cancel)
        {
            var files = new List<FileInfo>();
            for (int offset = -1; offset <= 7 && !cancel.IsCancellationRequested; offset++)
            {
                var day = DateTime.Today.AddDays(-offset);
                var directory = new DirectoryInfo(Path.Combine(sessions, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd")));
                if (!directory.Exists) continue;
                try { files.AddRange(directory.EnumerateFiles("rollout-*.jsonl")); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var newest = files.OrderByDescending(item => item.LastWriteTimeUtc).Take(8).ToArray();
            var paths = new HashSet<string>(newest.Select(file => file.FullName), StringComparer.OrdinalIgnoreCase);
            foreach (var path in cache.Keys.Where(path => !paths.Contains(path)).ToArray()) cache.Remove(path);

            QuotaSnapshot best = null;
            foreach (var file in newest)
            {
                if (cancel.IsCancellationRequested) break;
                CachedFile entry;
                try
                {
                    if (!cache.TryGetValue(file.FullName, out entry))
                    {
                        entry = new CachedFile();
                        cache.Add(file.FullName, entry);
                    }
                    long length = file.Length;
                    var modified = file.LastWriteTimeUtc;
                    if (entry.Length != length || entry.Modified != modified)
                    {
                        var update = ReadTail(file.FullName, cancel);
                        if (update != null) entry.Snapshot = update;
                        if (!cancel.IsCancellationRequested)
                        {
                            entry.Length = length;
                            entry.Modified = modified;
                        }
                    }
                    if (entry.Snapshot != null && (best == null || entry.Snapshot.Captured > best.Captured))
                        best = entry.Snapshot;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return best;
        }

        private QuotaSnapshot ReadTail(string path, CancellationToken cancel)
        {
            string tail;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                int size = (int)Math.Min(TailLimit, length);
                if (size == 0) return null;
                if (tailBuffer == null || tailBuffer.Length < size) tailBuffer = new byte[size];
                stream.Position = length - size;
                int total = 0;
                while (total < size && !cancel.IsCancellationRequested)
                {
                    int read = stream.Read(tailBuffer, total, size - total);
                    if (read == 0) break;
                    total += read;
                }
                if (cancel.IsCancellationRequested) return null;
                tail = Encoding.UTF8.GetString(tailBuffer, 0, total).TrimStart('\ufeff');
            }
            int end = tail.Length;
            while (end > 0 && !cancel.IsCancellationRequested)
            {
                int start = tail.LastIndexOf('\n', end - 1);
                if (end - start > 12 &&
                    tail.IndexOf("\"token_count\"", start + 1, end - start - 1, StringComparison.Ordinal) >= 0 &&
                    tail.IndexOf("\"rate_limits\"", start + 1, end - start - 1, StringComparison.Ordinal) >= 0)
                {
                    var record = Json.Parse(tail.Substring(start + 1, end - start - 1));
                    var payload = Json.Object(Json.Value(record, "payload"));
                    var limits = Json.Object(Json.Value(payload, "rate_limits"));
                    DateTimeOffset time;
                    if (Json.Text(record, "type") == "event_msg" && Json.Text(payload, "type") == "token_count" &&
                        limits != null && DateTimeOffset.TryParse(Json.Text(record, "timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out time))
                    {
                        var data = new QuotaSnapshot { Captured = time };
                        AddCodexWindow(data, limits, "primary", "主要额度", false);
                        AddCodexWindow(data, limits, "secondary", "次要额度", false);
                        if (data.Windows.Count > 0) return data;
                    }
                }
                end = start;
            }
            return null;
        }

        internal static QuotaSnapshot ReadAccountLimits(IDictionary<string, object> reply)
        {
            var result = Json.Object(Json.Value(reply, "result"));
            var byId = Json.Object(Json.Value(result, "rateLimitsByLimitId"));
            var limits = Json.Object(Json.Value(byId, "codex")) ?? Json.Object(Json.Value(result, "rateLimits"));
            var data = new QuotaSnapshot { Captured = DateTimeOffset.UtcNow, Source = "账号查询" };
            AddCodexWindow(data, limits, "primary", "主要额度", true);
            AddCodexWindow(data, limits, "secondary", "次要额度", true);
            return data.Windows.Count > 0 ? data : null;
        }

        private static void AddCodexWindow(QuotaSnapshot data, IDictionary<string, object> limits, string key,
            string fallback, bool account)
        {
            var window = Json.Object(Json.Value(limits, key));
            var used = Json.Number(window, account ? "usedPercent" : "used_percent");
            if (!used.HasValue || used < 0) return;
            var duration = Json.Number(window, account ? "windowDurationMins" : "window_minutes");
            var pool = Json.Text(limits, account ? "limitName" : "limit_name");
            data.Windows.Add(new QuotaWindow
            {
                Name = (string.IsNullOrWhiteSpace(pool) ? "" : ShortLabel(pool) + " · ") +
                    (duration.HasValue ? DurationLabel(duration.Value) : fallback),
                RemainingPercent = Math.Max(0, Math.Min(100, 100 - used.Value)),
                Reset = UnixTime(Json.Number(window, account ? "resetsAt" : "resets_at"))
            });
        }

        private static string DurationLabel(double minutes)
        {
            if (minutes > 0 && minutes % 1440 == 0) return (minutes / 1440).ToString("0") + " 天";
            if (minutes > 0 && minutes % 60 == 0) return (minutes / 60).ToString("0") + " 小时";
            return minutes.ToString("0") + " 分钟";
        }

        internal static DateTimeOffset? UnixTime(double? seconds)
        {
            if (!seconds.HasValue || seconds < 0 || seconds > 253402300799L) return null;
            return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds((long)seconds.Value);
        }

        internal static string ShortLabel(string value)
        {
            var label = new string(value.Where(character => !char.IsControl(character)).Take(54).ToArray()).Trim();
            return label.Length == 0 ? "额度" : label;
        }

        private sealed class CachedFile
        {
            internal long Length = -1;
            internal DateTime Modified;
            internal QuotaSnapshot Snapshot;
        }
    }

    internal static class AntigravitySnapshots
    {
        internal const string FileName = "antigravity-quota.json";
        private static readonly DateTimeOffset MinValidReset = new DateTimeOffset(1970, 1, 2, 0, 0, 0, TimeSpan.Zero);

        internal static DateTimeOffset? ParseReset(string text)
        {
            DateTimeOffset reset;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out reset))
                return null;
            return reset > MinValidReset ? reset : (DateTimeOffset?)null;
        }

        internal static string FormatBucketLabel(string id, string groupName = null, string window = null, string bucketName = null)
        {
            string known = KnownBucketLabel(id);
            if (known != null) return known;
            string group = FormatGroupName(groupName);
            string win = FormatWindowName(window, bucketName);
            if (!string.IsNullOrWhiteSpace(group) && !string.IsNullOrWhiteSpace(win))
                return CodexSnapshots.ShortLabel(group + " · " + win);
            if (!string.IsNullOrWhiteSpace(group)) return CodexSnapshots.ShortLabel(group);
            if (!string.IsNullOrWhiteSpace(win)) return CodexSnapshots.ShortLabel(win);
            return CodexSnapshots.ShortLabel(id ?? "额度");
        }

        private static string KnownBucketLabel(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            switch (id.Trim().ToLowerInvariant())
            {
                case "gemini-weekly": return "Gemini · 周额度";
                case "gemini-5h": return "Gemini · 5 小时";
                case "3p-weekly": return "Claude & GPT · 周额度";
                case "3p-5h": return "Claude & GPT · 5 小时";
                default: return null;
            }
        }

        private static string FormatGroupName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var trimmed = name.Trim();
            if (string.Equals(trimmed, "Gemini Models", StringComparison.OrdinalIgnoreCase)) return "Gemini";
            if (string.Equals(trimmed, "Claude and GPT models", StringComparison.OrdinalIgnoreCase)) return "Claude & GPT";
            if (trimmed.EndsWith(" models", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 7)
                return trimmed.Substring(0, trimmed.Length - 7).Trim();
            return trimmed;
        }

        private static string FormatWindowName(string window, string bucketName)
        {
            if (!string.IsNullOrWhiteSpace(window))
            {
                switch (window.Trim().ToLowerInvariant())
                {
                    case "5h": return "5 小时";
                    case "weekly": return "周额度";
                    case "daily": return "日额度";
                    case "monthly": return "月额度";
                }
            }
            if (!string.IsNullOrWhiteSpace(bucketName))
            {
                var lower = bucketName.Trim().ToLowerInvariant();
                if (lower.IndexOf("five hour", StringComparison.Ordinal) >= 0 || lower.IndexOf("5-hour", StringComparison.Ordinal) >= 0)
                    return "5 小时";
                if (lower.IndexOf("weekly", StringComparison.Ordinal) >= 0)
                    return "周额度";
                return bucketName.Trim();
            }
            return window ?? "";
        }

        private static int BucketSortRank(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return 100;
            switch (id.Trim().ToLowerInvariant())
            {
                case "gemini-weekly": return 0;
                case "gemini-5h": return 1;
                case "3p-weekly": return 2;
                case "3p-5h": return 3;
                default: return 50;
            }
        }

        internal static QuotaSnapshot Read(LocalStore store)
        {
            var cache = Json.Parse(store.Read(FileName));
            DateTimeOffset time;
            if (cache == null || !DateTimeOffset.TryParse(Json.Text(cache, "captured"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out time)) return null;
            var source = Json.Text(cache, "source");
            var data = new QuotaSnapshot
            {
                Captured = time,
                Source = string.IsNullOrWhiteSpace(source) ? "CLI 状态栏" : CodexSnapshots.ShortLabel(source)
            };
            var windows = Json.Object(Json.Value(cache, "quota"));
            if (windows == null) return null;
            var ordered = windows
                .Select((bucket, index) => new { Bucket = bucket, Rank = BucketSortRank(bucket.Key), Index = index })
                .OrderBy(item => item.Rank)
                .ThenBy(item => item.Index);
            foreach (var item in ordered)
            {
                var bucket = item.Bucket;
                var entry = Json.Object(bucket.Value);
                var ratio = Json.Number(entry, "remaining_fraction");
                var amount = Json.Number(entry, "remaining_amount");
                if (ratio < 0 || ratio > 1) ratio = null;
                if (amount < 0) amount = null;
                if (!ratio.HasValue && !amount.HasValue) continue;
                var label = Json.Text(entry, "label");
                data.Windows.Add(new QuotaWindow
                {
                    Name = string.IsNullOrWhiteSpace(label) ? FormatBucketLabel(bucket.Key) : CodexSnapshots.ShortLabel(label),
                    RemainingPercent = ratio.HasValue ? ratio.Value * 100 : (double?)null,
                    RemainingAmount = amount,
                    Reset = ParseReset(Json.Text(entry, "reset_time"))
                });
            }
            return data.Windows.Count > 0 ? data : null;
        }

        internal static bool SaveNormalized(LocalStore store, DateTimeOffset now, string source, IDictionary<string, object> safe)
        {
            if (safe == null || safe.Count == 0) return false;
            var previous = Json.Parse(store.Read(FileName));
            DateTimeOffset captured;
            if (DateTimeOffset.TryParse(Json.Text(previous, "captured"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out captured) &&
                now >= captured && now - captured < TimeSpan.FromMinutes(1) &&
                string.Equals(Json.Text(previous, "source"), source, StringComparison.Ordinal) &&
                SameQuota(Json.Object(Json.Value(previous, "quota")), safe))
                return true;
            return store.Write(FileName, Json.Serialize(new { captured = now.ToString("o"), source = source, quota = safe }));
        }

        internal static int Capture(LocalStore store)
        {
            const int maxPayload = 512 * 1024;
            string input;
            try
            {
                using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                {
                    var buffer = new char[maxPayload + 1];
                    int length = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (length > maxPayload) return 2;
                    input = new string(buffer, 0, length);
                }
            }
            catch (IOException) { return 2; }
            var state = Json.Parse(input);
            if (Json.Text(state, "product") != "antigravity") return 2;
            var source = Json.Object(Json.Value(state, "quota"));
            if (source == null || source.Count == 0) return 0;
            if (!ProviderProcesses.Read().Antigravity) return 0;
            var now = DateTimeOffset.UtcNow;
            var safe = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var item in source.OrderBy(pair => BucketSortRank(pair.Key)).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(32))
            {
                var entry = Json.Object(item.Value);
                if (Json.Flag(entry, "disabled")) continue;
                var ratio = Json.Number(entry, "remaining_fraction");
                var amount = Json.Number(entry, "remaining_amount");
                if (ratio < 0 || ratio > 1) ratio = null;
                if (amount < 0) amount = null;
                if (!ratio.HasValue && !amount.HasValue) continue;
                var normalized = new Dictionary<string, object>();
                normalized.Add("label", FormatBucketLabel(item.Key));
                if (ratio.HasValue) normalized.Add("remaining_fraction", ratio.Value);
                else normalized.Add("remaining_amount", amount.Value);
                var reset = ParseReset(Json.Text(entry, "reset_time"));
                if (reset.HasValue)
                    normalized.Add("reset_time", reset.Value.ToUniversalTime().ToString("o"));
                else
                {
                    var seconds = Json.Number(entry, "reset_in_seconds");
                    if (seconds > 0 && seconds <= TimeSpan.FromDays(30).TotalSeconds)
                        normalized.Add("reset_time", now.AddSeconds(Math.Round(seconds.Value, 0)).ToString("o"));
                }
                safe[CodexSnapshots.ShortLabel(item.Key)] = normalized;
            }
            if (safe.Count == 0) return 0;
            return SaveNormalized(store, now, "CLI 状态栏", safe) ? 0 : 3;
        }

        private static bool SameQuota(IDictionary<string, object> previous, IDictionary<string, object> current)
        {
            if (previous == null || previous.Count != current.Count) return false;
            foreach (var bucket in current)
            {
                var old = Json.Object(Json.Value(previous, bucket.Key));
                var next = Json.Object(bucket.Value);
                if (old == null || Json.Number(old, "remaining_fraction") != Json.Number(next, "remaining_fraction") ||
                    Json.Number(old, "remaining_amount") != Json.Number(next, "remaining_amount") ||
                    !string.Equals(Json.Text(old, "label"), Json.Text(next, "label"), StringComparison.Ordinal))
                    return false;

                string oldReset = Json.Text(old, "reset_time"), newReset = Json.Text(next, "reset_time");
                if (oldReset == newReset) continue;
                DateTimeOffset oldTime, newTime;
                if (!DateTimeOffset.TryParse(oldReset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out oldTime) ||
                    !DateTimeOffset.TryParse(newReset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out newTime) ||
                    Math.Abs((oldTime - newTime).TotalSeconds) > 3) return false;
            }
            return true;
        }

        internal static string StatusLineSettings(string exe)
        {
            bool needsQuotes = exe.IndexOfAny(new[] { ' ', '\t', '&', '|', '<', '>', '^', '%', '"' }) >= 0;
            string command = needsQuotes ? "\"\"" + exe + "\" --antigravity-statusline\"" : exe + " --antigravity-statusline";
            return Json.Serialize(new
            {
                statusLine = new { type = "command", command = command, stack_with_default = true }
            });
        }
    }

    internal enum AntigravityReadError
    {
        None, MissingCli, Storage, Launch, Login, Timeout, Connection, RateLimit, InvalidResponse, Unavailable
    }

    internal sealed class AntigravityReadResult
    {
        internal QuotaSnapshot Snapshot;
        internal AntigravityReadError Error;
        internal DateTimeOffset Finished;
        internal int? ExitCode;

        internal bool CanRetry
        {
            get
            {
                return Error == AntigravityReadError.Timeout || Error == AntigravityReadError.Connection ||
                    Error == AntigravityReadError.Unavailable;
            }
        }

        internal string Message
        {
            get
            {
                switch (Error)
                {
                    case AntigravityReadError.MissingCli: return "同名目录缺少 CLI";
                    case AntigravityReadError.Storage: return "同名目录不可写";
                    case AntigravityReadError.Launch: return "CLI 启动失败";
                    case AntigravityReadError.Login: return "CLI 登录态不可用";
                    case AntigravityReadError.Timeout: return "读取超时";
                    case AntigravityReadError.Connection: return "额度服务连接失败";
                    case AntigravityReadError.RateLimit: return "额度查询暂被限流";
                    case AntigravityReadError.InvalidResponse: return "额度返回无效";
                    default: return "额度暂不可用";
                }
            }
        }
    }

    internal sealed class AntigravityCli
    {
        internal const string RelativeExecutable = @"agy\bin\agy.exe";
        internal const string LastFailureFile = "antigravity-last-failure.json";
        private const string UsageArguments =
            "--disable-slash-commands=false --settings \"{\\\"disableSlashCommands\\\":false,\\\"agentMode\\\":\\\"plan\\\"}\" " +
            "--mode plan -p \"/usage\" --output-format json --print-timeout 40s";
        private int activePid;

        internal int ActivePid
        {
            get { return Interlocked.CompareExchange(ref activePid, 0, 0); }
        }

        internal string FindExecutable(LocalStore store)
        {
            try
            {
                var executable = Path.Combine(store.CheckedDirectory(false, "agy", "bin"), "agy.exe");
                return (File.GetAttributes(executable) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                    ? executable : null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        private static ProcessStartInfo UsageProcess(LocalStore store, string agy)
        {
            // Scope redirects to the quota process so the user's GUI and terminal profiles keep their own settings.
            var work = store.CheckedDirectory(true, "agy", "run");
            var profile = store.CheckedDirectory(true, "agy", "run", "profile");
            var local = store.CheckedDirectory(true, "agy", "run", "profile", "AppData", "Local");
            var roaming = store.CheckedDirectory(true, "agy", "run", "profile", "AppData", "Roaming");
            var temp = store.CheckedDirectory(true, "agy", "run", "temp");
            var gemini = store.CheckedDirectory(true, "agy", "run", "profile", ".gemini");
            store.CheckedDirectory(true, "agy", "run", "profile", ".gemini", "antigravity-cli");
            var log = Path.Combine(work, "quota.log");
            if (File.Exists(log) && (File.GetAttributes(log) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The quota log is a reparse point.");

            var start = new ProcessStartInfo
            {
                FileName = agy,
                Arguments = UsageArguments + " --log-file \"" + log + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = work
            };

            var environment = start.EnvironmentVariables;
            environment["USERPROFILE"] = profile;
            environment["LOCALAPPDATA"] = local;
            environment["APPDATA"] = roaming;
            environment["TEMP"] = temp;
            environment["TMP"] = temp;
            environment.Remove("HOMEDRIVE");
            environment.Remove("HOMEPATH");
            environment.Remove("GEMINI_API_KEY");
            environment["ANTIGRAVITY_GEMINI_DIR"] = gemini;
            environment["ANTIGRAVITY_APP_DATA_DIR"] = store.CheckedDirectory(true, "agy", "run", "app-data");
            environment["ANTIGRAVITY_EXECUTABLE_DATA_DIR"] = store.CheckedDirectory(true, "agy", "run", "execution-data");
            environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
            environment["CI"] = "true";
            return start;
        }

        internal AntigravityReadResult Query(LocalStore store, CancellationToken cancel)
        {
            var started = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            var result = new AntigravityReadResult { Error = AntigravityReadError.Unavailable };
            result.Snapshot = ReadUsage(store, cancel, result);
            result.Finished = DateTimeOffset.UtcNow;
            if (result.Snapshot != null) result.Error = AntigravityReadError.None;
            if (result.Error != AntigravityReadError.None && !cancel.IsCancellationRequested)
            {
                store.Write(LastFailureFile, Json.Serialize(new
                {
                    started = started.ToString("o"),
                    finished = result.Finished.ToString("o"),
                    elapsed_ms = elapsed.ElapsedMilliseconds,
                    exit_code = result.ExitCode,
                    reason = result.Error.ToString(),
                    message = result.Message
                }));
            }
            return result;
        }

        private QuotaSnapshot ReadUsage(LocalStore store, CancellationToken cancel, AntigravityReadResult result)
        {
            if (cancel.IsCancellationRequested) return null;
            string agy = FindExecutable(store);
            if (string.IsNullOrEmpty(agy))
            {
                result.Error = AntigravityReadError.MissingCli;
                return null;
            }

            string stdout = null;
            string stderr = null;
            ProcessStartInfo start;
            try { start = UsageProcess(store, agy); }
            catch (IOException) { result.Error = AntigravityReadError.Storage; return null; }
            catch (UnauthorizedAccessException) { result.Error = AntigravityReadError.Storage; return null; }

            try
            {
                using (var process = new Process { StartInfo = start })
                {
                    if (!process.Start())
                    {
                        result.Error = AntigravityReadError.Launch;
                        return null;
                    }
                    Interlocked.Exchange(ref activePid, process.Id);
                    using (cancel.Register(delegate { TryKill(process); }))
                    {
                        try { process.StandardInput.Close(); }
                        catch (IOException) { }
                        var outTask = process.StandardOutput.ReadToEndAsync();
                        var errTask = process.StandardError.ReadToEndAsync();
                        if (!process.WaitForExit(45000))
                        {
                            TryKill(process);
                            result.Error = AntigravityReadError.Timeout;
                            return null;
                        }
                        result.ExitCode = process.ExitCode;
                        if (outTask.Wait(2000)) stdout = outTask.Result;
                        if (errTask.Wait(2000)) stderr = errTask.Result;
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                result.Error = AntigravityReadError.Launch;
                return null;
            }
            catch (InvalidOperationException)
            {
                result.Error = AntigravityReadError.Launch;
                return null;
            }
            catch (IOException) { return null; }
            catch (AggregateException) { return null; }
            finally
            {
                Interlocked.Exchange(ref activePid, 0);
            }

            if (cancel.IsCancellationRequested) return null;
            int jsonStart = string.IsNullOrWhiteSpace(stdout) ? -1 : stdout.IndexOf('{');
            var root = jsonStart < 0 ? null : Json.Parse(stdout.Substring(jsonStart));
            string state = Json.Text(root, "status");
            if (result.ExitCode == 0 && string.Equals(state, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseUsage(root, DateTimeOffset.UtcNow, store, cancel);
                if (parsed != null) return parsed;
                result.Error = AntigravityReadError.InvalidResponse;
                return null;
            }

            // Prefer the terminal response. Startup warnings can also occur during a successful run.
            var reason = ClassifyError(Json.Text(root, "error"));
            result.Error = reason == AntigravityReadError.Unavailable ? ClassifyError(stderr) : reason;
            return null;
        }

        private static AntigravityReadError ClassifyError(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return AntigravityReadError.Unavailable;
            detail = detail.ToLowerInvariant();
            if (detail.Contains("deadline exceeded") || detail.Contains("timed out") || detail.Contains("timeout"))
                return AntigravityReadError.Timeout;
            if (detail.Contains("http 429") || detail.Contains("status code 429") ||
                detail.Contains("too many requests") || detail.Contains("rate limit"))
                return AntigravityReadError.RateLimit;
            if (detail.Contains("authentication required") || detail.Contains("not logged in") ||
                detail.Contains("invalid_grant") || detail.Contains("login required") ||
                detail.Contains("please sign in") || detail.Contains("missing credentials") || detail.Contains("unauthenticated"))
                return AntigravityReadError.Login;
            if (detail.Contains("connection refused") || detail.Contains("connection reset") ||
                detail.Contains("no such host") || detail.Contains("network is unreachable") ||
                detail.Contains("service unavailable") || detail.Contains("bad gateway"))
                return AntigravityReadError.Connection;
            return AntigravityReadError.Unavailable;
        }

        private static QuotaSnapshot ParseUsage(IDictionary<string, object> root, DateTimeOffset now, LocalStore store,
            CancellationToken cancel)
        {
            var command = Json.Object(Json.Value(root, "command"));
            var usage = Json.Object(Json.Value(root, "usage"));
            if (!string.Equals(Json.Text(command, "name"), "usage", StringComparison.Ordinal) ||
                Json.Number(root, "num_turns") != 0 || Json.Number(usage, "total_tokens") != 0)
                return null;

            var safe = new Dictionary<string, object>(StringComparer.Ordinal);
            var snapshot = new QuotaSnapshot { Captured = now, Source = "CLI /usage" };
            var data = Json.Object(Json.Value(command, "data"));
            var groups = Json.Value(data, "groups") as object[];
            if (groups != null)
            {
                foreach (var groupObj in groups)
                {
                    var group = Json.Object(groupObj);
                    if (group == null) continue;
                    string groupName = Json.Text(group, "name");
                    var buckets = Json.Value(group, "buckets") as object[];
                    if (buckets == null) continue;
                    foreach (var bucketObj in buckets)
                    {
                        var bucket = Json.Object(bucketObj);
                        if (bucket == null || Json.Flag(bucket, "disabled")) continue;
                        var ratio = Json.Number(bucket, "remaining_fraction");
                        var amount = Json.Number(bucket, "remaining_amount");
                        if (ratio < 0 || ratio > 1) ratio = null;
                        if (amount < 0) amount = null;
                        if (!ratio.HasValue && !amount.HasValue) continue;
                        string id = Json.Text(bucket, "id");
                        string window = Json.Text(bucket, "window");
                        string bucketName = Json.Text(bucket, "name");
                        string label = AntigravitySnapshots.FormatBucketLabel(id, groupName, window, bucketName);
                        string key = CodexSnapshots.ShortLabel(string.IsNullOrWhiteSpace(id) ? label : id);
                        var reset = AntigravitySnapshots.ParseReset(Json.Text(bucket, "reset_time"));
                        var normalized = new Dictionary<string, object>();
                        normalized.Add("label", label);
                        if (ratio.HasValue) normalized.Add("remaining_fraction", ratio.Value);
                        else normalized.Add("remaining_amount", amount.Value);
                        if (reset.HasValue) normalized.Add("reset_time", reset.Value.ToUniversalTime().ToString("o"));
                        safe[key] = normalized;
                        snapshot.Windows.Add(new QuotaWindow
                        {
                            Name = label,
                            RemainingPercent = ratio.HasValue ? ratio.Value * 100 : (double?)null,
                            RemainingAmount = amount,
                            Reset = reset
                        });
                    }
                }
            }

            if (snapshot.Windows.Count == 0 || cancel.IsCancellationRequested) return null;
            AntigravitySnapshots.SaveNormalized(store, now, "CLI /usage", safe);
            return snapshot;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
