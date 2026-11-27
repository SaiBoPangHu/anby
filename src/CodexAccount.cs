using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AnbyPet
{
    internal sealed class CodexAccount
    {
        private const int TimeoutMs = 30000;
        private const string Initialize = "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":" +
            "{\"name\":\"anbypet\",\"title\":\"AnbyPet\",\"version\":\"1.0.0\"}}}";
        private const string Initialized = "{\"method\":\"initialized\"}";
        private const string RateLimits = "{\"id\":2,\"method\":\"account/rateLimits/read\"}";
        private int activePid;

        internal int ActivePid { get { return Volatile.Read(ref activePid); } }

        internal QuotaSnapshot Read(LocalStore store, string executable, CancellationToken cancel, out string issue)
        {
            issue = "账号额度暂不可用";
            if (string.IsNullOrEmpty(executable) || cancel.IsCancellationRequested) return null;
            try
            {
                using (var process = new Process { StartInfo = StartInfo(store, executable) })
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!process.Start()) return null;
                    Volatile.Write(ref activePid, process.Id);
                    try
                    {
                        process.BeginErrorReadLine();
                        var elapsed = Stopwatch.StartNew();
                        var input = process.StandardInput;
                        input.NewLine = "\n";
                        input.WriteLine(Initialize);
                        input.Flush();
                        var reply = Receive(process.StandardOutput, 1, elapsed, cancel);
                        if (Json.Object(Json.Value(reply, "result")) != null)
                        {
                            input.WriteLine(Initialized);
                            input.WriteLine(RateLimits);
                            input.Flush();
                            reply = Receive(process.StandardOutput, 2, elapsed, cancel);
                        }
                        var snapshot = CodexSnapshots.ReadAccountLimits(reply);
                        if (snapshot != null) issue = null;
                        else if (elapsed.ElapsedMilliseconds >= TimeoutMs) issue = "账号查询超时";
                        return snapshot;
                    }
                    finally
                    {
                        Stop(process);
                        Volatile.Write(ref activePid, 0);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
            catch (AggregateException error)
            {
                if (!(error.InnerException is IOException)) throw;
            }
            return null;
        }

        private static ProcessStartInfo StartInfo(LocalStore store, string executable)
        {
            var folder = store.CheckedDirectory(true, "codex-query");
            var database = store.CheckedDirectory(true, "codex-query", "db");
            var logs = store.CheckedDirectory(true, "codex-query", "log");
            var temp = store.CheckedDirectory(true, "codex-query", "temp");
            var arguments = "app-server --stdio --disable plugins --disable apps " +
                "-c analytics.enabled=false -c history.persistence=\\\"none\\\"" +
                PathOption("sqlite_home", database) + PathOption("log_dir", logs);
            // Reuse the installed client's model catalog at startup to avoid refreshing it for a quota-only request.
            var catalog = Path.Combine(CodexSnapshots.Profile, "models_cache.json");
            if (File.Exists(catalog)) arguments += PathOption("model_catalog_json", catalog);
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["TEMP"] = temp;
            start.EnvironmentVariables["TMP"] = temp;
            start.EnvironmentVariables["TMPDIR"] = temp;
            return start;
        }

        private static string PathOption(string option, string path)
        {
            return " -c \"" + option + "=\\\"" + path.Replace('\\', '/') + "\\\"\"";
        }

        private static IDictionary<string, object> Receive(StreamReader output, int id, Stopwatch elapsed,
            CancellationToken cancel)
        {
            int remaining;
            while ((remaining = TimeoutMs - (int)elapsed.ElapsedMilliseconds) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var line = output.ReadLineAsync();
                if (!line.Wait(remaining, cancel) || line.Result == null) return null;
                var reply = Json.Parse(line.Result);
                if (Json.Number(reply, "id") == id &&
                    (Json.Value(reply, "result") != null || Json.Value(reply, "error") != null))
                    return reply;
            }
            return null;
        }

        private static void Stop(Process process)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            try
            {
                if (process.WaitForExit(1000)) return;
                process.Kill();
                process.WaitForExit(1000);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }
}
