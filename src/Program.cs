using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("AnbyPet")]
[assembly: AssemblyProduct("AnbyPet")]
[assembly: AssemblyDescription("Windows tray monitor and desktop pet")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AnbyPet
{
    internal static class Program
    {
        internal const string ControlWindowName = "AnbyPet.RunningInstance.1";
        internal const int ShowDashboard = 0x8001;

        [STAThread]
        private static int Main(string[] args)
        {
            string exe = Application.ExecutablePath;
            var store = new LocalStore(exe);
            if (args.Length == 1 && args[0] == "--antigravity-statusline")
                return AntigravitySnapshots.Capture(store);

            bool startedAtLogon = args.Length == 1 && args[0] == "--startup";
            bool first;
            using (var instance = new Mutex(true, @"Local\AnbyPet.RunningInstance.1", out first))
            {
                if (!first)
                {
                    if (!startedAtLogon)
                    {
                        for (int attempt = 0; attempt < 20; attempt++)
                        {
                            var window = FindWindow(null, ControlWindowName);
                            if (window != IntPtr.Zero)
                            {
                                PostMessage(window, ShowDashboard, IntPtr.Zero, IntPtr.Zero);
                                break;
                            }
                            Thread.Sleep(50);
                        }
                    }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var context = new TrayApp(exe, store, startedAtLogon))
                    Application.Run(context);
                return 0;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const int QuietInterval = 5000;
        private const int OpenInterval = 1000;
        private const int CodexQuotaInterval = 60000;
        private const int AntigravityCliOpenInterval = 60000;
        private const int AntigravityCliQuietInterval = 300000;
        private const int AntigravityRetryDelay = 15000;
        private const string SettingsFile = "settings.json";
        private readonly string exe;
        private readonly LocalStore store;
        private readonly Icon icon;
        private readonly Dashboard dashboard;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ContextMenuStrip applicationMenu;
        private readonly ToolStripMenuItem startupMenu, petMenu;
        private readonly ControlWindow control;
        private readonly LoadMonitor monitor;
        private readonly FrameRateMonitor frameRate;
        private readonly Timer timer;
        private readonly Timer fullscreenTimer;
        private readonly int ownProcess;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly PanelData readings = new PanelData();
        private readonly CodexSnapshots codex = new CodexSnapshots();
        private readonly CodexAccount codexAccount = new CodexAccount();
        private readonly AntigravityCli antigravityCli = new AntigravityCli();
        private readonly QuotaPoll codexPoll = new QuotaPoll();
        private readonly QuotaPoll antigravityCachePoll = new QuotaPoll();
        private readonly QuotaPoll antigravityCliPoll = new QuotaPoll();
        private readonly HashSet<DesktopApp> appActions = new HashSet<DesktopApp>();
        private string codexIssue;
        private AntigravityReadResult antigravityAttempt;
        private long nextProcesses;
        private bool disposed;
        private DesktopPet pet;

        internal TrayApp(string exe, LocalStore store, bool startedAtLogon)
        {
            this.exe = exe;
            this.store = store;
            using (var process = Process.GetCurrentProcess()) ownProcess = process.Id;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AnbyPet.Tray.ico"))
            using (var original = new Icon(stream))
                icon = (Icon)original.Clone();

            applicationMenu = new ContextMenuStrip { ShowImageMargin = false };
            applicationMenu.Items.AddRange(ApplicationCommands());
            dashboard = new Dashboard(icon, applicationMenu);
            monitor = new LoadMonitor();
            frameRate = new FrameRateMonitor(store);
            control = new ControlWindow(ShowPanel, monitor.RefreshAdapters);
            startupMenu = new ToolStripMenuItem("登录时启动", null, delegate { ToggleStartup(); });
            petMenu = new ToolStripMenuItem("显示安比", null, delegate { TogglePet(); });
            menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("打开监测面板", null, delegate { ShowPanel(); }));
            var applications = new ToolStripMenuItem("应用管理");
            applications.DropDownItems.AddRange(ApplicationCommands());
            menu.Items.Add(applications);
            menu.Items.Add(petMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startupMenu);
            menu.Items.Add(new ToolStripMenuItem("设置 Antigravity 同步", null, delegate { AntigravitySetup(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitThread(); }));

            tray = new NotifyIcon { Icon = icon, Text = "AnbyPet", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowPanel(); };

            LoadStartupPreference(startedAtLogon);
            timer = new Timer { Interval = OpenInterval };
            timer.Tick += Tick;
            fullscreenTimer = new Timer { Interval = 1000 };
            fullscreenTimer.Tick += delegate { PlaceFullscreenWindows(); };
            dashboard.VisibleChanged += DashboardVisibilityChanged;
            timer.Start();
        }

        private void LoadStartupPreference(bool startedAtLogon)
        {
            var settings = Json.Parse(store.Read(SettingsFile));
            var registered = Startup.RegisteredCommand();
            bool wanted = Json.Flag(settings, "launchAtLogon", true);
            var previousExe = Json.Text(settings, "executable");
            bool relocated = settings != null && !string.Equals(previousExe, exe, StringComparison.OrdinalIgnoreCase);
            if (!startedAtLogon && (settings == null || relocated))
            {
                if (SaveStartup(wanted))
                    Startup.Apply(exe, wanted);
                registered = Startup.RegisteredCommand();
            }
            startupMenu.Checked = string.Equals(registered, Startup.Command(exe), StringComparison.OrdinalIgnoreCase);
        }

        private bool SaveStartup(bool enabled)
        {
            bool result = store.Write(SettingsFile, Json.Serialize(new { launchAtLogon = enabled, executable = exe }));
            readings.StorageError = store.Error;
            return result;
        }

        private void ToggleStartup()
        {
            bool wanted = !startupMenu.Checked;
            if (!SaveStartup(wanted))
            {
                MessageBox.Show("无法写入 EXE 旁边的同名数据目录。", "安比桌宠", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Startup.Apply(exe, wanted))
            {
                SaveStartup(!wanted);
                MessageBox.Show("Windows 未能更新登录启动项。", "安比桌宠", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            startupMenu.Checked = wanted;
        }

        private void AntigravitySetup()
        {
            string configuration = AntigravitySnapshots.StatusLineSettings(exe);
            try { Clipboard.SetText(configuration); }
            catch (ExternalException)
            {
                MessageBox.Show("剪贴板暂时不可用，请重试。", "Antigravity 同步", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string cli = antigravityCli.FindExecutable(store);
            if (antigravityCliPoll.Active)
            {
                antigravityCliPoll.Next = 0;
                Sample();
            }
            string message = !string.IsNullOrEmpty(cli)
                ? "已检测到本机 Antigravity CLI，启动 Antigravity 后会自动通过 /usage 同步额度。\n\n" +
                  "同时已复制 statusLine 配置，若在终端中使用 agy，可将其合并到 ~/.gemini/antigravity-cli/settings.json 以开启实时推送。"
                : "请将 Windows 版 agy.exe 放入以下位置：\n" +
                  Path.Combine(store.Folder, AntigravityCli.RelativeExecutable) + "\n\n" +
                  "同时也已复制 statusLine 配置，可合并到 ~/.gemini/antigravity-cli/settings.json。";
            MessageBox.Show(message, "Antigravity 同步", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void TogglePet()
        {
            if (pet == null)
            {
                pet = new DesktopPet();
                pet.VisibleChanged += delegate { UpdateFullscreenTimer(); };
            }
            if (pet.Visible) pet.Hide();
            else pet.Open();
            petMenu.Text = pet.Visible ? "隐藏安比" : "显示安比";
        }

        private ToolStripItem[] ApplicationCommands()
        {
            return new ToolStripItem[]
            {
                new ToolStripMenuItem("启动 Codex", null, delegate { ControlApp(DesktopApp.Codex, true); }),
                new ToolStripMenuItem("退出 Codex", null, delegate { ControlApp(DesktopApp.Codex, false); }),
                new ToolStripSeparator(),
                new ToolStripMenuItem("启动 Antigravity", null, delegate { ControlApp(DesktopApp.Antigravity, true); }),
                new ToolStripMenuItem("退出 Antigravity", null, delegate { ControlApp(DesktopApp.Antigravity, false); })
            };
        }

        private async void ControlApp(DesktopApp app, bool launch)
        {
            if (disposed || !appActions.Add(app)) return;
            string name = DesktopApps.Name(app);
            try
            {
                if (launch)
                {
                    DesktopApps.Launch(app);
                    return;
                }
                string executable = app == DesktopApp.Antigravity ? DesktopApps.AntigravityExecutable() : null;
                var exit = await Task.Run(() => DesktopExit.Capture(app, executable));
                if (disposed) return;
                if (exit.Count == 0)
                {
                    tray.ShowBalloonTip(1800, "安比桌宠", name + " 桌面端当前未运行。", ToolTipIcon.Info);
                    return;
                }
                if (app == DesktopApp.Codex &&
                    !ConfirmAppExit("退出 Codex 也会关闭本机的 ChatGPT 桌面端。要继续吗？", name))
                    return;
                if (app == DesktopApp.Codex)
                {
                    if (codexPoll.Request != null) codexPoll.Request.Cancel();
                }
                else
                {
                    if (antigravityCachePoll.Request != null) antigravityCachePoll.Request.Cancel();
                    if (antigravityCliPoll.Request != null) antigravityCliPoll.Request.Cancel();
                }
                int remaining = await Task.Run(() => exit.RequestClose());
                if (disposed) return;
                if (remaining != 0)
                {
                    if (!ConfirmAppExit(name + " 仍有 " + remaining + " 个所属进程在运行。强制结束会中断任务，并可能丢失未保存的内容。要继续吗？", name))
                        return;
                    remaining = await Task.Run(() => exit.ForceClose());
                }
                if (remaining == 0 && !disposed)
                    remaining = await Task.Run(() => DesktopExit.Capture(app, executable).Count);
                if (disposed) return;
                if (remaining == 0) tray.ShowBalloonTip(1800, "安比桌宠", name + " 桌面端已退出。", ToolTipIcon.Info);
                else AppMessage(name + " 仍有 " + remaining + " 个所属进程在运行。", MessageBoxIcon.Warning);
            }
            catch (Exception error)
            {
                if (!disposed) AppMessage(name + " 操作失败。\n" + error.GetBaseException().Message, MessageBoxIcon.Warning);
            }
            finally
            {
                appActions.Remove(app);
                nextProcesses = 0;
                if (!disposed) Sample();
            }
        }

        private bool ConfirmAppExit(string message, string app)
        {
            return MessageBox.Show(dashboard.Visible ? dashboard : null, message, "退出 " + app,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void AppMessage(string message, MessageBoxIcon icon)
        {
            MessageBox.Show(dashboard.Visible ? dashboard : null, message, "应用管理", MessageBoxButtons.OK, icon);
        }

        private void ShowPanel()
        {
            if (disposed) return;
            dashboard.Open(readings);
            Sample();
        }

        private void DashboardVisibilityChanged(object sender, EventArgs e)
        {
            UpdateTimerInterval();
            bool visible = dashboard.Visible;
            antigravityCliPoll.Reschedule(clock.ElapsedMilliseconds,
                visible ? AntigravityCliOpenInterval : AntigravityCliQuietInterval, visible);
            UpdateFullscreenTimer();
        }

        private void UpdateFullscreenTimer()
        {
            if (disposed) return;
            fullscreenTimer.Enabled = dashboard.Visible || (pet != null && pet.Visible);
            if (fullscreenTimer.Enabled) PlaceFullscreenWindows();
        }

        private void PlaceFullscreenWindows()
        {
            using (PetDisplays.PhysicalCoordinates.Enter())
            {
                var primary = PetDisplays.Primary();
                if (primary.Handle == IntPtr.Zero) return;
                var secondary = PetDisplays.PrimaryIsFullscreen(primary, ownProcess)
                    ? PetDisplays.Secondary(primary) : new PetDisplays.Display();
                dashboard.PlaceForFullscreen(secondary);
                if (pet != null && pet.Visible)
                {
                    var occupied = secondary.Handle != IntPtr.Zero && dashboard.Visible
                        ? PetDisplays.WindowBounds(dashboard.Handle) : Rectangle.Empty;
                    if (!secondary.WorkArea.IntersectsWith(occupied)) occupied = Rectangle.Empty;
                    pet.PlaceForFullscreen(secondary, occupied);
                }
            }
        }

        private void UpdateTimerInterval()
        {
            int interval = dashboard.Visible ? OpenInterval : QuietInterval;
            if (timer.Interval != interval) timer.Interval = interval;
        }

        private void Tick(object sender, EventArgs e)
        {
            Sample();
            UpdateTimerInterval();
        }

        private void Sample()
        {
            long time = clock.ElapsedMilliseconds;
            readings.Cpu = monitor.Cpu();
            readings.GpuLoad = monitor.Gpu();
            readings.Fps = frameRate.Sample(time, dashboard.Visible);
            if (time >= nextProcesses)
            {
                readings.Providers = ProviderProcesses.Read(antigravityCli.ActivePid, codexAccount.ActivePid);
                codexPoll.Observe(readings.Providers.Codex);
                antigravityCachePoll.Observe(readings.Providers.Antigravity);
                antigravityCliPoll.Observe(readings.Providers.Antigravity);
                if (!readings.Providers.Codex) codexIssue = null;
                if (!readings.Providers.Antigravity) antigravityAttempt = null;
                nextProcesses = time + QuietInterval;
            }
            if (!appActions.Contains(DesktopApp.Codex)) ReadCodexQuota(time);
            if (!appActions.Contains(DesktopApp.Antigravity))
            {
                ReadQuota(antigravityCachePoll, time, QuietInterval, delegate { return AntigravitySnapshots.Read(store); });
                ReadAntigravityQuota(time);
            }
            UpdateUi();
        }

        private void ReadCodexQuota(long time)
        {
            if (!codexPoll.CanRead(time)) return;
            var executable = readings.Providers.CodexExecutable;
            var previous = codexPoll.Latest;
            string issue = null;
            ReadQuota(codexPoll, time, CodexQuotaInterval,
                delegate(CancellationToken token)
                {
                    return codexAccount.Read(store, executable, token, out issue) ??
                        NewestSnapshot(previous, codex.Read(token));
                },
                delegate
                {
                    codexIssue = issue == null ? null : issue + " · " + Board.QuotaTime(DateTimeOffset.UtcNow);
                });
        }

        private void ReadAntigravityQuota(long time)
        {
            var poll = antigravityCliPoll;
            if (!poll.CanRead(time)) return;
            int interval = dashboard.Visible ? AntigravityCliOpenInterval : AntigravityCliQuietInterval;
            AntigravityReadResult attempt = null;
            ReadQuota(poll, time, interval,
                delegate(CancellationToken token)
                {
                    attempt = antigravityCli.Query(store, token);
                    return attempt.Snapshot;
                },
                delegate
                {
                    antigravityAttempt = attempt ?? new AntigravityReadResult
                    {
                        Error = AntigravityReadError.Unavailable,
                        Finished = DateTimeOffset.UtcNow
                    };
                    if (antigravityAttempt.Snapshot != null)
                        poll.ResetRetry();
                    else if (antigravityAttempt.CanRetry)
                        poll.ScheduleRetry(clock.ElapsedMilliseconds, AntigravityRetryDelay);
                });
        }

        private async void ReadQuota(QuotaPoll poll, long time, int interval,
            Func<CancellationToken, QuotaSnapshot> reader, Action onComplete = null)
        {
            if (!poll.CanRead(time)) return;
            var request = new CancellationTokenSource();
            var token = request.Token;
            poll.Request = request;
            poll.Started(time, interval);
            QuotaSnapshot result = null;
            try { result = await Task.Run(() => reader(token), token); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                poll.Request = null;
                bool canceled = request.IsCancellationRequested;
                request.Dispose();
                if (!disposed && poll.Active && !canceled)
                {
                    if (result != null) poll.Latest = result;
                    if (onComplete != null) onComplete();
                    UpdateUi();
                }
            }
        }

        private static QuotaSnapshot NewestSnapshot(QuotaSnapshot first, QuotaSnapshot second)
        {
            if (first == null) return second;
            if (second == null) return first;
            return second.Captured >= first.Captured ? second : first;
        }

        private void UpdateUi()
        {
            readings.Codex = codexPoll.Latest;
            readings.CodexStatus = codexIssue;
            if (readings.Codex == null && codexPoll.Request != null && codexIssue == null)
                readings.CodexStatus = "正在查询额度";
            readings.Antigravity = NewestSnapshot(antigravityCachePoll.Latest, antigravityCliPoll.Latest);
            readings.AntigravityStatus = null;
            if (antigravityAttempt != null && antigravityAttempt.Error != AntigravityReadError.None &&
                (readings.Antigravity == null || readings.Antigravity.Captured < antigravityAttempt.Finished))
                readings.AntigravityStatus = antigravityAttempt.Message + " · " + Board.QuotaTime(antigravityAttempt.Finished);
            else if (readings.Antigravity == null && antigravityCliPoll.Request != null)
                readings.AntigravityStatus = "正在获取额度";
            readings.StorageError = store.Error;
            string tooltip = "CPU " + Board.Percentage(readings.Cpu) + "  ·  GPU " +
                Board.Percentage(readings.GpuLoad) + "  ·  " + Board.FrameRate(readings.Fps);
            if (readings.StorageError != null) tooltip += "\n同名数据目录不可写";
            if (!string.Equals(tooltip, tray.Text, StringComparison.Ordinal))
                tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            if (dashboard.Visible) dashboard.UpdateReadings(readings);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                if (codexPoll.Request != null) codexPoll.Request.Cancel();
                if (antigravityCachePoll.Request != null) antigravityCachePoll.Request.Cancel();
                if (antigravityCliPoll.Request != null) antigravityCliPoll.Request.Cancel();
                timer.Dispose();
                fullscreenTimer.Dispose();
                dashboard.VisibleChanged -= DashboardVisibilityChanged;
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                applicationMenu.Dispose();
                control.DestroyHandle();
                monitor.Dispose();
                frameRate.Dispose();
                if (pet != null) pet.Dispose();
                dashboard.Dispose();
                icon.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class QuotaPoll
        {
            internal bool Active;
            internal long Next;
            internal long LastAttempt;
            internal CancellationTokenSource Request;
            internal QuotaSnapshot Latest;
            private bool retryUsed;
            private long retryDue;

            internal bool CanRead(long now) { return Active && Request == null && now >= Next; }

            internal void Started(long now, int interval)
            {
                LastAttempt = now;
                Next = now + interval;
                retryDue = 0;
            }

            internal void ScheduleRetry(long now, int delay)
            {
                if (retryUsed || now + delay >= Next) return;
                retryUsed = true;
                retryDue = now + delay;
                Next = retryDue;
            }

            internal void ResetRetry()
            {
                retryUsed = false;
                retryDue = 0;
            }

            internal void Reschedule(long now, int interval, bool refreshOnOpen)
            {
                if (!Active) return;
                bool dueOnOpen = refreshOnOpen && Request == null && now - LastAttempt >= 30000;
                Next = LastAttempt == 0 || dueOnOpen ? now : LastAttempt + interval;
                if (retryDue != 0) Next = Math.Min(Next, retryDue);
            }

            internal void Observe(bool active)
            {
                if (Active == active) return;
                Active = active;
                if (!active)
                {
                    Latest = null;
                    LastAttempt = 0;
                    ResetRetry();
                    if (Request != null) Request.Cancel();
                }
                Next = 0;
            }
        }

        private sealed class ControlWindow : NativeWindow
        {
            private readonly Action show, displayChanged;

            internal ControlWindow(Action show, Action displayChanged)
            {
                this.show = show;
                this.displayChanged = displayChanged;
                CreateHandle(new CreateParams { Caption = Program.ControlWindowName });
            }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == Program.ShowDashboard) show();
                const int displayChange = 0x007E;
                const int powerBroadcast = 0x0218;
                const int resumeAutomatic = 0x0012;
                if (message.Msg == displayChange ||
                    (message.Msg == powerBroadcast && message.WParam.ToInt32() == resumeAutomatic))
                    displayChanged();
                if (message.Msg == 0x0010)
                {
                    Application.ExitThread();
                    return;
                }
                base.WndProc(ref message);
            }
        }
    }
}
