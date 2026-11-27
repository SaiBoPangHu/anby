using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AnbyPet
{
    internal sealed class FrameRateMonitor : IDisposable
    {
        private const int EmptyRetryDelay = 15000;
        private const int CaptureTimeout = 6000;
        private readonly LocalStore store;
        private readonly int ownPid;
        private readonly string session = "AnbyPet-FPS-" + Guid.NewGuid().ToString("N");
        private Task<double?> request;
        private string collector;
        private IntPtr sourceWindow;
        private int sourcePid, requestPid;
        private long nextSample;
        private double? latest;
        private volatile bool stopped;

        internal FrameRateMonitor(LocalStore store)
        {
            this.store = store;
            using (var current = Process.GetCurrentProcess()) ownPid = current.Id;
        }

        internal double? Sample(long now, bool panelOpen)
        {
            int pid = ForegroundProcess(panelOpen);
            if (pid != sourcePid)
            {
                sourcePid = pid;
                latest = null;
                nextSample = now;
            }

            if (request != null && request.IsCompleted)
            {
                if (requestPid == sourcePid)
                {
                    latest = request.Result;
                    nextSample = latest.HasValue ? now : now + EmptyRetryDelay;
                }
                request = null;
            }

            if (!stopped && sourcePid != 0 && request == null && now >= nextSample)
            {
                requestPid = pid;
                request = Task.Run(() => Capture(pid));
            }
            return latest;
        }

        private int ForegroundProcess(bool panelOpen)
        {
            var window = GetForegroundWindow();
            int pid = WindowProcess(window);
            int shell = WindowProcess(GetShellWindow());
            if ((pid == ownPid && panelOpen) || (pid != 0 && pid == shell && IsTrayWindow(window)))
            {
                // Keep the renderer selected while its readout or the notification menu has focus.
                window = sourceWindow;
                pid = WindowProcess(window);
            }

            if (pid == 0 || pid == ownPid || pid == shell || !IsWindowVisible(window) || IsIconic(window))
            {
                sourceWindow = IntPtr.Zero;
                return 0;
            }
            sourceWindow = window;
            return pid;
        }

        private static int WindowProcess(IntPtr window)
        {
            int pid = 0;
            if (window != IntPtr.Zero) GetWindowThreadProcessId(window, out pid);
            return pid;
        }

        private static bool IsTrayWindow(IntPtr window)
        {
            var name = new StringBuilder(48);
            GetClassName(window, name, name.Capacity);
            string windowClass = name.ToString();
            return windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd" ||
                windowClass == "NotifyIconOverflowWindow";
        }

        private double? Capture(int pid)
        {
            try
            {
                if (stopped) return null;
                if (collector == null) collector = PrepareCollector();
                var data = new DisplayIntervals(pid);
                var arguments = "--process_id " + pid.ToString(CultureInfo.InvariantCulture) +
                    " --output_stdout --no_console_stats --exclude_dropped --v1_metrics --no_track_gpu --no_track_input" +
                    " --timed 2 --terminate_after_timed --terminate_on_proc_exit --session_name " + session;
                using (var process = new Process { StartInfo = StartInfo(arguments, true) })
                {
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { data.Add(e.Data); };
                    if (!process.Start()) return null;
                    try
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        var elapsed = Stopwatch.StartNew();
                        while (!process.WaitForExit(125))
                        {
                            if (stopped || elapsed.ElapsedMilliseconds >= CaptureTimeout) return null;
                        }
                        process.WaitForExit();
                        return process.ExitCode == 0 ? data.Rate() : null;
                    }
                    finally
                    {
                        if (!process.HasExited) StopCapture(process);
                    }
                }
            }
            catch (Win32Exception) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private ProcessStartInfo StartInfo(string arguments, bool readOutput)
        {
            string directory = Path.GetDirectoryName(collector);
            var start = new ProcessStartInfo
            {
                FileName = collector,
                Arguments = arguments,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = readOutput,
                RedirectStandardError = readOutput
            };
            start.EnvironmentVariables["TEMP"] = directory;
            start.EnvironmentVariables["TMP"] = directory;
            return start;
        }

        private void StopCapture(Process capture)
        {
            try
            {
                // Stop only the ETW session created by this monitor before terminating its reader.
                using (var clean = Process.Start(StartInfo("--terminate_existing_session --session_name " + session, false)))
                {
                    if (clean != null && !clean.WaitForExit(800)) clean.Kill();
                }
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            if (!capture.WaitForExit(500))
            {
                capture.Kill();
                capture.WaitForExit(300);
            }
        }

        private string PrepareCollector()
        {
            string directory = store.CheckedDirectory(true, "fps");
            string executable = Path.Combine(directory, "PresentMon-2.6.0-x64.exe");
            ExtractResource("AnbyPet.PresentMon.LICENSE.txt", Path.Combine(directory, "PresentMon-LICENSE.txt"));
            ExtractResource("AnbyPet.PresentMon.exe", executable);
            return executable;
        }

        private static void ExtractResource(string resource, string path)
        {
            using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (embedded == null) throw new IOException("Missing frame collector resource.");
                bool exists = File.Exists(path);
                if (exists)
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The frame collector file is a reparse point.");
                    using (var installed = File.OpenRead(path))
                    using (var hash = SHA256.Create())
                    {
                        if (installed.Length == embedded.Length &&
                            hash.ComputeHash(installed).SequenceEqual(hash.ComputeHash(embedded))) return;
                    }
                    embedded.Position = 0;
                }
                string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write)) embedded.CopyTo(output);
                    if (exists) File.Replace(stage, path, null);
                    else File.Move(stage, path);
                }
                finally { if (File.Exists(stage)) File.Delete(stage); }
            }
        }

        public void Dispose()
        {
            stopped = true;
            if (request != null) request.Wait(1800);
        }

        private sealed class DisplayIntervals
        {
            private readonly int targetPid;
            private readonly Dictionary<string, IntervalTotal> chains = new Dictionary<string, IntervalTotal>();
            private int pidColumn = -1, chainColumn, timeColumn, columnCount;

            internal DisplayIntervals(int pid) { targetPid = pid; }

            internal void Add(string line)
            {
                if (string.IsNullOrEmpty(line)) return;
                var columns = line.Split(',');
                if (pidColumn < 0)
                {
                    int pid = Find(columns, "ProcessID");
                    chainColumn = Find(columns, "SwapChainAddress");
                    timeColumn = Find(columns, "msBetweenDisplayChange");
                    if (pid < 0 || chainColumn < 0 || timeColumn < 0) return;
                    pidColumn = pid;
                    columnCount = columns.Length;
                    return;
                }
                // PresentMon's process-name field can contain a comma. All tracked fields follow it.
                int extra = columns.Length - columnCount;
                int id;
                double time;
                if (extra < 0 || !int.TryParse(columns[pidColumn + extra], out id) || id != targetPid ||
                    !double.TryParse(columns[timeColumn + extra], NumberStyles.Float, CultureInfo.InvariantCulture, out time) ||
                    time <= 0 || double.IsNaN(time) || double.IsInfinity(time)) return;
                string chain = columns[chainColumn + extra];
                IntervalTotal total;
                if (!chains.TryGetValue(chain, out total))
                {
                    total = new IntervalTotal();
                    chains.Add(chain, total);
                }
                total.Frames++;
                total.Milliseconds += time;
            }

            internal double? Rate()
            {
                var primary = chains.Values.OrderByDescending(chain => chain.Frames).FirstOrDefault();
                return primary == null || primary.Frames < 3
                    ? (double?)null : 1000.0 * primary.Frames / primary.Milliseconds;
            }

            private static int Find(string[] columns, string name)
            {
                return Array.FindIndex(columns, column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase));
            }

            private sealed class IntervalTotal
            {
                internal int Frames;
                internal double Milliseconds;
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window,
            StringBuilder name, int capacity);
    }
}
