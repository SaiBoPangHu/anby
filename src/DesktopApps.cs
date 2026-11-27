using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AnbyPet
{
    internal enum DesktopApp
    {
        Codex,
        Antigravity
    }

    internal static class DesktopApps
    {
        internal const string CodexPackage = "OpenAI.Codex_2p2nqsd0c76g0";
        private const string AntigravityId = "electron.app.Antigravity";

        internal static string Name(DesktopApp app)
        {
            return app == DesktopApp.Codex ? "Codex" : "Antigravity";
        }

        internal static void Launch(DesktopApp app)
        {
            WithEntry(app, item => Invoke(item, "InvokeVerb", Type.Missing));
        }

        internal static string AntigravityExecutable()
        {
            var path = WithEntry(DesktopApp.Antigravity,
                item => Invoke(item, "ExtendedProperty", "System.Link.TargetParsingPath")) as string;
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) ||
                !string.Equals(Path.GetFileName(path), "Antigravity.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("找不到 Antigravity 桌面端的程序路径。");
            return Path.GetFullPath(path);
        }

        private static object WithEntry(DesktopApp app, Func<object, object> operation)
        {
            object shell = null, folder = null, item = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                folder = Invoke(shell, "NameSpace", "shell:AppsFolder");
                string id = app == DesktopApp.Codex ? CodexPackage + "!App" : AntigravityId;
                if (folder != null) item = Invoke(folder, "ParseName", id);
                if (item == null) throw new InvalidOperationException("开始菜单中找不到 " + Name(app) + " 的启动入口。");
                return operation(item);
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        private static object Invoke(object target, string method, params object[] arguments)
        {
            return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding,
                null, target, arguments, CultureInfo.InvariantCulture);
        }
    }

    internal sealed class DesktopExit
    {
        private const uint QueryProcess = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const uint Terminate = 0x0001;
        private readonly ProcessRecord[] roots;
        private readonly Dictionary<int, ProcessRecord> owned;
        private readonly string[] privateFolders;
        private readonly int session;

        private DesktopExit(int session, ProcessRecord[] roots, string[] folders, Dictionary<int, ProcessRecord> snapshot)
        {
            this.session = session;
            this.roots = roots;
            privateFolders = folders;
            owned = roots.ToDictionary(process => process.Id);
            TrackChildren(snapshot);
        }

        internal int Count { get { return owned.Count; } }

        internal static DesktopExit Capture(DesktopApp app, string antigravityExecutable)
        {
            if (app == DesktopApp.Antigravity && string.IsNullOrEmpty(antigravityExecutable))
                throw new ArgumentException("Missing Antigravity executable path.", "antigravityExecutable");
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            var snapshot = Snapshot(session);
            bool codex = app == DesktopApp.Codex;
            var roots = snapshot.Values.Where(p => codex ? IsCodex(p) :
                string.Equals(p.Path, antigravityExecutable, StringComparison.OrdinalIgnoreCase)).ToArray();
            var folders = roots.Select(p => Path.GetDirectoryName(p.Path)).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (codex)
            {
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex"));
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes"));
            }
            return new DesktopExit(session, roots, folders.Select(folder => folder.TrimEnd('\\') + "\\")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), snapshot);
        }

        internal int RequestClose()
        {
            foreach (var target in roots)
            {
                var handle = OpenProcess(QueryProcess, false, target.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (!IsSameProcess(handle, target)) continue;
                    using (var process = Process.GetProcessById(target.Id))
                        process.CloseMainWindow();
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally { CloseHandle(handle); }
            }
            return WaitForExit(4500);
        }

        internal int ForceClose()
        {
            TrackChildren(Snapshot(session));
            foreach (var target in owned.Values.OrderBy(process => process.Created))
            {
                var handle = OpenProcess(QueryProcess | Terminate, false, target.Id);
                if (handle == IntPtr.Zero) continue;
                try { if (IsSameProcess(handle, target)) TerminateProcess(handle, 1); }
                finally { CloseHandle(handle); }
            }
            return WaitForExit(2500);
        }

        private int WaitForExit(int limit)
        {
            var elapsed = Stopwatch.StartNew();
            while (owned.Values.Any(IsRunning) && elapsed.ElapsedMilliseconds < limit) Thread.Sleep(125);
            TrackChildren(Snapshot(session));
            return owned.Values.Count(IsRunning);
        }

        private void TrackChildren(Dictionary<int, ProcessRecord> snapshot)
        {
            foreach (var candidate in snapshot.Values)
            {
                if (owned.ContainsKey(candidate.Id) || string.IsNullOrEmpty(candidate.Path) ||
                    !privateFolders.Any(folder => candidate.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var child = candidate;
                ProcessRecord parent, tracked;
                while (child.Parent != 0)
                {
                    bool parentExists = snapshot.TryGetValue(child.Parent, out parent);
                    if (owned.TryGetValue(child.Parent, out tracked) && tracked.Created < child.Created &&
                        (!parentExists || tracked.Created == parent.Created))
                    {
                        owned.Add(candidate.Id, candidate);
                        break;
                    }
                    if (!parentExists || parent.Created >= child.Created) break;
                    child = parent;
                }
            }
        }

        private static Dictionary<int, ProcessRecord> Snapshot(int session)
        {
            var result = new Dictionary<int, ProcessRecord>();
            var query = "SELECT ProcessId, ParentProcessId, CreationDate, ExecutablePath FROM Win32_Process WHERE SessionId = " +
                session.ToString(CultureInfo.InvariantCulture);
            using (var search = new ManagementObjectSearcher(query))
            using (var entries = search.Get())
            {
                foreach (ManagementObject entry in entries)
                using (entry)
                {
                    var created = entry["CreationDate"] as string;
                    if (string.IsNullOrEmpty(created)) continue;
                    var record = new ProcessRecord
                    {
                        Id = (int)(uint)entry["ProcessId"],
                        Parent = (int)(uint)entry["ParentProcessId"],
                        Created = ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime().ToFileTimeUtc(),
                        Path = entry["ExecutablePath"] as string
                    };
                    if (record.Id > 0) result[record.Id] = record;
                }
            }
            return result;
        }

        private static bool IsCodex(ProcessRecord target)
        {
            string file = Path.GetFileName(target.Path);
            if (!string.Equals(file, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(file, "Codex.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var handle = OpenProcess(QueryProcess, false, target.Id);
            if (handle == IntPtr.Zero) return false;
            try
            {
                uint length = 128;
                var package = new StringBuilder((int)length);
                return IsSameProcess(handle, target) && GetPackageFamilyName(handle, ref length, package) == 0 &&
                    string.Equals(package.ToString(), DesktopApps.CodexPackage, StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(handle); }
        }

        private static bool IsRunning(ProcessRecord target)
        {
            var handle = OpenProcess(QueryProcess | Synchronize, false, target.Id);
            if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
            try { return IsSameProcess(handle, target) && WaitForSingleObject(handle, 0) != 0; }
            finally { CloseHandle(handle); }
        }

        private static bool IsSameProcess(IntPtr handle, ProcessRecord target)
        {
            long created, exited, kernel, user;
            // WMI creation timestamps have microsecond precision.
            return GetProcessTimes(handle, out created, out exited, out kernel, out user) &&
                Math.Abs(created - target.Created) < 10;
        }

        private sealed class ProcessRecord
        {
            internal int Id, Parent;
            internal long Created;
            internal string Path;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access,
            bool inherit, int processId);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr handle, out long created,
            out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackageFamilyName(IntPtr handle, ref uint length, StringBuilder packageFamily);
    }
}
