using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HoverVideoPreview
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--self-test")
            {
                Environment.ExitCode = SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "验证结果"), false);
                return;
            }
            if (args.Length > 0 && args[0] == "--visual-test")
            {
                Environment.ExitCode = SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "验证结果"), true);
                return;
            }
            if (args.Length > 0 && args[0] == "--browser-test")
            {
                Environment.ExitCode = SelfTest.RunBrowser(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "验证结果"));
                return;
            }
            if (args.Length > 0 && args[0] == "--background-test")
            {
                Environment.ExitCode = SelfTest.RunBackground(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "验证结果"));
                return;
            }
            if (args.Length > 0 && args[0] == "--bridge-fixture")
            {
                Environment.ExitCode = SelfTest.RunBridgeFixture(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "验证结果"));
                return;
            }
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\HoverVideoPreview_20261010", out created))
            {
                if (!created) { MessageBox.Show("工具已经在运行。请双击任务栏右下角的预览工具图标打开设置。", "悬停视频预览"); return; }
                try
                {
                    using (PreviewContext context = new PreviewContext(false)) Application.Run(context);
                }
                catch (Exception error)
                {
                    MessageBox.Show("无法启动：" + error.Message, "悬停视频预览", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private readonly Action<int> action;
        internal readonly bool PauseRegistered, SettingsRegistered;
        internal HotkeyWindow(Action<int> callback)
        {
            action = callback;
            CreateParams parameters = new CreateParams(); parameters.Caption = "HoverVideoPreviewHotkeys"; parameters.Parent = new IntPtr(-3);
            CreateHandle(parameters);
            PauseRegistered = Native.RegisterHotKey(Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.P);
            SettingsRegistered = Native.RegisterHotKey(Handle, 2, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.O);
        }
        protected override void WndProc(ref Message message)
        { if (message.Msg == Native.WM_HOTKEY) action(message.WParam.ToInt32()); base.WndProc(ref message); }
        public void Dispose()
        {
            if (PauseRegistered) Native.UnregisterHotKey(Handle, 1);
            if (SettingsRegistered) Native.UnregisterHotKey(Handle, 2);
            DestroyHandle();
        }
    }

    internal sealed class PreviewContext : ApplicationContext
    {
        internal readonly PreviewSettings Settings;
        internal readonly PreviewForm Preview;
        internal readonly BrowserBridge Bridge;
        internal string BridgeError { get; private set; }
        private readonly MarkerForm marker;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HoverGate gate = new HoverGate();
        private readonly string settingsPath;
        private readonly bool testMode;
        private NotifyIcon tray;
        private HotkeyWindow hotkeys;
        private SettingsForm configuration;
        private ToolStripMenuItem pauseMenu, markerMenu;
        private bool running, paused;
        private uint sourcePid;
        private long nextLayout;
        private string status = "请选择播放视频的窗口。";
        internal bool Suppress;
        internal bool Exiting { get; private set; }
        internal bool Running { get { return running; } }
        internal bool Paused { get { return paused; } }
        internal string Status { get { return status; } }
        internal Func<Point> PointerProvider = Native.CursorPosition;
        internal event EventHandler StatusChanged;

        internal PreviewContext(bool forTesting) : this(forTesting, null) { }
        internal PreviewContext(bool forTesting, BrowserBridge suppliedBridge)
        {
            testMode = forTesting;
            settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml");
            Settings = forTesting ? new PreviewSettings() : PreviewSettings.Load(settingsPath);
            if (suppliedBridge != null) Bridge = suppliedBridge;
            else if (!forTesting)
            {
                try { Bridge = new BrowserBridge(52637); }
                catch (Exception error) { BridgeError = "后台模式暂时无法启动：" + error.Message; }
            }
            Preview = new PreviewForm(Settings); marker = new MarkerForm();
            marker.SettingsRequested += delegate { ShowSettings(); };
            timer = new System.Windows.Forms.Timer(); timer.Interval = 40;
            timer.Tick += delegate { Tick(); }; timer.Start();
            if (!forTesting)
            {
                SetupTray();
                hotkeys = new HotkeyWindow(delegate(int id) { if (id == 1) TogglePause(); else ShowSettings(); });
                configuration = new SettingsForm(this);
                if (!hotkeys.PauseRegistered || !hotkeys.SettingsRegistered) SetStatus("部分快捷键已被其他应用占用，请使用右下角托盘菜单。" );
                configuration.Show();
            }
        }
        private void SetupTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("设置 / 选择视频窗口", null, delegate { ShowSettings(); });
            pauseMenu = new ToolStripMenuItem("暂停预览  Ctrl+Alt+P", null, delegate { TogglePause(); }); menu.Items.Add(pauseMenu);
            markerMenu = new ToolStripMenuItem("显示触发区标记", null, delegate
            { Settings.ShowMarker = !Settings.ShowMarker; SaveSettings(); UpdateMarker(); }); menu.Items.Add(markerMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            tray = new NotifyIcon(); tray.Icon = SystemIcons.Application; tray.Text = "悬停视频预览"; tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowSettings(); }; tray.Visible = true;
        }
        internal void Start(WindowItem item)
        {
            if (item == null) throw new InvalidOperationException("请先选择播放视频的窗口。");
            running = false; HideTransient();
            Preview.Attach(item.Handle); sourcePid = item.ProcessId;
            Settings.BrowserMode = false;
            Settings.LastWindowTitle = item.Title; Settings.LastProcessName = item.ProcessName;
            paused = false; running = true; nextLayout = 0;
            SaveSettings(); SetStatus("已启动。鼠标移入触发区显示，移开隐藏。"); UpdateMarker();
        }
        internal void StartBrowser()
        {
            if (Bridge == null) throw new InvalidOperationException(BridgeError ?? "后台画面接收器未启动。");
            HideTransient(); Preview.UseBrowser(); Settings.BrowserMode = true;
            paused = false; running = true; nextLayout = 0;
            SaveSettings(); SetStatus("后台模式已启动，等待浏览器扩展连接。"); UpdateMarker();
        }
        internal void SaveSettings()
        {
            if (testMode) return;
            try { Settings.Save(settingsPath); }
            catch (Exception error) { SetStatus("设置暂时无法保存：" + error.Message); }
        }
        internal void HideTransient() { gate.Reset(); Preview.Hide(); marker.Hide(); }
        internal void TogglePause()
        {
            paused = !paused; HideTransient();
            SetStatus(paused ? "预览已暂停。Ctrl+Alt+P 可恢复。" : (running ? "预览已恢复。" : "请先选择窗口并启动。"));
            UpdateMarker();
        }
        internal void ShowSettings()
        {
            if (testMode || Exiting) return;
            HideTransient();
            if (configuration == null || configuration.IsDisposed) configuration = new SettingsForm(this);
            configuration.Sync(); configuration.Show(); configuration.WindowState = FormWindowState.Normal; configuration.Activate();
        }
        internal void SettingsHidden() { gate.Reset(); UpdateMarker(); }
        private bool UserIsConfiguring { get { return Suppress || (configuration != null && configuration.Visible); } }
        internal void Tick()
        {
            bool ready = running && !paused && !UserIsConfiguring;
            BrowserFrame browserFrame = null;
            if (ready && Preview.BrowserMode)
            {
                browserFrame = Bridge == null ? null : Bridge.Latest;
                if (browserFrame == null || (DateTime.UtcNow - browserFrame.Received).TotalSeconds > 3)
                { ready = false; SetStatus("等待后台画面：请在视频标签页中点击扩展的“开始后台预览”。"); }
                else SetStatus("后台视频已连接。切换页面或最小化浏览器后仍可悬停观看。");
            }
            else if (ready)
            {
                uint actualPid; Native.GetWindowThreadProcessId(Preview.Source, out actualPid);
                if (!Native.IsWindow(Preview.Source) || actualPid != sourcePid)
                {
                    running = false; ready = false; Preview.Detach();
                    SetStatus("视频窗口已关闭，请重新选择窗口。");
                    if (tray != null) { tray.BalloonTipTitle = "悬停视频预览"; tray.BalloonTipText = status; tray.ShowBalloonTip(2000); }
                }
                else if (Native.IsIconic(Preview.Source) || !Native.IsWindowVisible(Preview.Source))
                { ready = false; SetStatus("视频窗口已最小化或隐藏，请恢复窗口。" ); }
            }
            Point pointer = PointerProvider();
            bool hovering = Settings.HotArea.Contains(pointer) || (Settings.KeepOnPreview && Preview.Visible && Preview.Bounds.Contains(pointer));
            bool show = gate.Update(hovering, ready, clock.ElapsedMilliseconds, Settings.ShowDelay, Settings.HideDelay);
            if (show)
            {
                try
                {
                    if (Preview.BrowserMode) Preview.SetBrowserFrame(browserFrame);
                    if (!Preview.Visible || clock.ElapsedMilliseconds >= nextLayout)
                    { Preview.UpdateLayout(); nextLayout = clock.ElapsedMilliseconds + 500; }
                    Preview.ShowQuietly();
                }
                catch (Exception error) { running = false; HideTransient(); SetStatus(error.Message); }
            }
            else if (Preview.Visible) Preview.Hide();
            UpdateMarker();
        }
        private void UpdateMarker()
        {
            bool show = running && !paused && !UserIsConfiguring && Settings.ShowMarker;
            if (show)
            { marker.Bounds = Settings.HotArea; if (!marker.Visible) marker.Show(); }
            else if (marker.Visible) marker.Hide();
            if (pauseMenu != null) pauseMenu.Checked = paused;
            if (markerMenu != null) markerMenu.Checked = Settings.ShowMarker;
        }
        private void SetStatus(string value)
        {
            if (status == value) return; status = value;
            if (tray != null) tray.Text = paused ? "悬停视频预览 · 已暂停" : (running ? "悬停视频预览 · 运行中" : "悬停视频预览 · 等待设置");
            if (StatusChanged != null) StatusChanged(this, EventArgs.Empty);
        }
        protected override void ExitThreadCore()
        { Exiting = true; HideTransient(); if (configuration != null) configuration.Close(); base.ExitThreadCore(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Exiting = true; timer.Stop(); timer.Dispose();
                if (hotkeys != null) hotkeys.Dispose();
                if (Bridge != null) Bridge.Dispose();
                if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
                Preview.Dispose(); marker.Dispose(); if (configuration != null) configuration.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
