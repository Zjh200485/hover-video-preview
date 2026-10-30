using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HoverVideoPreview
{
    internal static class SelfTest
    {
        private static readonly List<string> lines = new List<string>();
        private static int failed;
        private static readonly string testSession = "1b869768-25c8-40f9-8dfa-76f7660cdac8";
        private static long testFrameSequence;
        internal static int RunBridgeFixture(string directory)
        {
            Directory.CreateDirectory(directory);
            using (BrowserBridge bridge = new BrowserBridge(52637))
            using (PreviewContext context = new PreviewContext(true, bridge))
            using (System.Windows.Forms.Timer commands = new System.Windows.Forms.Timer())
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                context.Settings.SetHotArea(new Rectangle(work.Left + 45, work.Top + 25, 100, 28));
                context.Settings.ShowMarker = false; context.Settings.ShowDelay = context.Settings.HideDelay = 0;
                context.Settings.ResetCrop(); context.Settings.PreviewWidth = 440;
                context.PointerProvider = delegate { return new Point(context.Settings.HotX + 5, context.Settings.HotY + 5); };
                context.StartBrowser();
                File.WriteAllText(Path.Combine(directory, "fixture-code.txt"), bridge.ConnectionCode);
                string commandPath = Path.Combine(directory, "fixture-command.txt"), previous = "";
                Stopwatch deadline = Stopwatch.StartNew(); commands.Interval = 80;
                commands.Tick += delegate
                {
                    if (deadline.ElapsedMilliseconds > 90000) { context.ExitThread(); return; }
                    if (!File.Exists(commandPath)) return;
                    string command;
                    try { command = File.ReadAllText(commandPath).Trim(); } catch (IOException) { return; }
                    if (command == previous) return; previous = command;
                    if (command == "stop") { context.ExitThread(); return; }
                    if (command == "hidden") context.PointerProvider = delegate { return new Point(work.Right - 2, work.Bottom - 2); };
                    if (command == "show") context.PointerProvider = delegate { return new Point(context.Settings.HotX + 5, context.Settings.HotY + 5); };
                    if (command.StartsWith("capture-") && command.Length < 50)
                    {
                        bool safe = true; foreach (char c in command) if (!Char.IsLetterOrDigit(c) && c != '-') safe = false;
                        if (!safe) return;
                        context.Tick();
                        if (context.Preview.Visible)
                            using (Bitmap image = Capture(context.Preview.Bounds)) image.Save(Path.Combine(directory, command + ".png"), ImageFormat.Png);
                        File.WriteAllText(Path.Combine(directory, command + ".txt"), "visible=" + context.Preview.Visible + " frames=" + (bridge.Latest == null ? 0 : bridge.Latest.Sequence));
                    }
                };
                commands.Start(); Application.Run(context);
            }
            return 0;
        }
        internal static int RunBackground(string directory)
        {
            Directory.CreateDirectory(directory); lines.Clear(); failed = 0;
            lines.Add("后台视频接收与悬停显示验证"); lines.Add("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                using (BrowserBridge bridge = new BrowserBridge(0))
                using (PreviewContext context = new PreviewContext(true, bridge))
                {
                    string token = bridge.ConnectionCode.Split(':')[1];
                    int status; string body = BridgeRequest(bridge, "/status", "GET", "wrong-key", null, out status);
                    Check("错误连接码无法读取接收器或写入画面", status == 403 && bridge.Latest == null);
                    body = BridgeRequest(bridge, "/status", "GET", token, null, out status);
                    Check("正确连接码可完成版本握手", status == 200 && body.Contains("\"version\":3"));
                    BridgeRequest(bridge, "/start", "POST", token, null, out status);
                    Check("新采集会话可建立", status == 200);
                    BridgeRequest(bridge, "/frame", "POST", token, new byte[] {1,2,3,4}, out status);
                    Check("非图像数据不会成为预览帧", status == 415 && bridge.Latest == null);
                    Rectangle work = Screen.PrimaryScreen.WorkingArea;
                    context.Settings.SetHotArea(new Rectangle(work.Left + 40, work.Top + 30, 100, 28));
                    context.Settings.ShowDelay = context.Settings.HideDelay = 0; context.Settings.ShowMarker = false;
                    context.Settings.ResetCrop(); context.Settings.PreviewWidth = 360;
                    Point pointer = new Point(work.Left + 2, work.Bottom - 2); context.PointerProvider = delegate { return pointer; };
                    context.StartBrowser(); Pump(60);
                    Check("未连接浏览器时不显示过期画面", !context.Preview.Visible);
                    BridgeRequest(bridge, "/frame", "POST", token, MakeFrame(Color.RoyalBlue, "FRAME 1"), out status);
                    Check("实时画面可经本机接口接收", status == 200 && bridge.Latest != null);
                    Check("视频后台传输时没有悬停就保持隐藏", !context.Preview.Visible);
                    pointer = new Point(context.Settings.HotX + 8, context.Settings.HotY + 8); context.Tick(); Pump(80);
                    Check("后台模式无需源窗口即可悬停显示", context.Preview.Visible && context.Preview.Source == IntPtr.Zero);
                    using (Bitmap image = Capture(context.Preview.Bounds)) image.Save(Path.Combine(directory, "background-frame-1.png"), ImageFormat.Png);
                    BridgeRequest(bridge, "/frame", "POST", token, MakeFrame(Color.Crimson, "FRAME 2"), out status);
                    context.Tick(); Pump(80);
                    using (Bitmap image = Capture(context.Preview.Bounds))
                    {
                        image.Save(Path.Combine(directory, "background-frame-2.png"), ImageFormat.Png);
                        Color center = image.GetPixel(image.Width / 2, image.Height / 2);
                        Check("后台传来的新帧会替换旧帧", center.R > center.B + 60);
                    }
                    pointer = new Point(work.Left + 2, work.Bottom - 2); context.Tick();
                    Check("后台模式移开鼠标立即隐藏", !context.Preview.Visible);
                    pointer = new Point(context.Settings.HotX + 8, context.Settings.HotY + 8); context.Tick();
                    context.TogglePause(); Check("暂停可立即隐藏后台画面", !context.Preview.Visible);
                    context.TogglePause(); context.Tick(); Check("恢复后仍可悬停观看", context.Preview.Visible);
                    BridgeRequest(bridge, "/stop", "POST", token, null, out status); context.Tick();
                    Check("浏览器停止连接后清除画面并隐藏", status == 200 && !context.Preview.Visible && bridge.Latest == null);
                }
            }
            catch (Exception error) { failed++; lines.Add("FAIL " + error); }
            lines.Add("结果：" + (failed == 0 ? "全部通过" : failed + " 项失败"));
            File.WriteAllLines(Path.Combine(directory, "background-test.txt"), lines.ToArray(), new UTF8Encoding(true)); return failed == 0 ? 0 : 1;
        }
        private static byte[] MakeFrame(Color color, string text)
        {
            using (Bitmap bitmap = new Bitmap(640, 360))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = new Font("Microsoft YaHei UI", 20f))
            using (MemoryStream stream = new MemoryStream())
            { graphics.Clear(color); graphics.DrawString(text, font, Brushes.White, 25, 30); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); }
        }
        private static string BridgeRequest(BrowserBridge bridge, string path, string method, string token, byte[] bytes, out int status)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + bridge.Port + path);
            request.Proxy = null; request.Method = method; request.Timeout = 4000; request.Headers["X-Hover-Key"] = token;
            request.Headers["X-Hover-Session"] = testSession;
            if (path == "/frame") request.Headers["X-Hover-Sequence"] = Interlocked.Increment(ref testFrameSequence).ToString();
            if (method == "POST")
            { request.ContentType = "image/jpeg"; request.ContentLength = bytes == null ? 0 : bytes.Length; if (bytes != null) using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length); }
            HttpWebResponse response;
            try { response = (HttpWebResponse)request.GetResponse(); }
            catch (WebException error) { response = error.Response as HttpWebResponse; if (response == null) throw; }
            using (response) using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            { status = (int)response.StatusCode; return reader.ReadToEnd(); }
        }
        internal static int RunBrowser(string directory)
        {
            Directory.CreateDirectory(directory); lines.Clear(); failed = 0;
            lines.Add("浏览器实时预览验证"); lines.Add("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                List<WindowItem> candidates = Native.ListWindows().FindAll(delegate(WindowItem item)
                { return item.ProcessName == "msedge" && item.Title.StartsWith("悬停预览兼容性测试", StringComparison.Ordinal); });
                if (candidates.Count != 1) throw new InvalidOperationException("应先在一个独立的 Edge 窗口里播放本工具的测试视频，当前匹配窗口数：" + candidates.Count);
                WindowItem target = candidates[0];
                Native.RECT sourceBounds;
                if (!Native.GetWindowRect(target.Handle, out sourceBounds)) throw new InvalidOperationException("无法读取测试浏览器窗口。");
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                using (PreviewContext context = new PreviewContext(true))
                using (QuietForm cover = new QuietForm())
                {
                    context.Settings.SetHotArea(new Rectangle(work.Left + 30, work.Top + 24, 100, 24));
                    context.Settings.ShowMarker = false; context.Settings.ShowDelay = 0; context.Settings.HideDelay = 0;
                    context.Settings.PreviewWidth = 440;
                    context.PointerProvider = delegate { return new Point(context.Settings.HotX + 10, context.Settings.HotY + 10); };
                    context.Start(target); context.Tick(); Pump(300);
                    using (Bitmap first = Capture(context.Preview.Bounds))
                    {
                        Pump(600);
                        using (Bitmap second = Capture(context.Preview.Bounds))
                        {
                            first.Save(Path.Combine(directory, "browser-visible-1.png"), ImageFormat.Png);
                            second.Save(Path.Combine(directory, "browser-visible-2.png"), ImageFormat.Png);
                            double difference = ChangedFraction(first, second);
                            lines.Add("浏览器可见时帧变化比例：" + difference.ToString("P2"));
                            Check("浏览器实际视频画面持续更新", difference > .015);
                        }
                    }
                    cover.FormBorderStyle = FormBorderStyle.None; cover.StartPosition = FormStartPosition.Manual;
                    cover.Bounds = sourceBounds.ToRectangle(); cover.ShowInTaskbar = false; cover.TopMost = true;
                    cover.BackColor = Color.FromArgb(82, 87, 96); cover.Show();
                    context.Preview.ShowQuietly(); Pump(1400);
                    using (Bitmap first = Capture(context.Preview.Bounds))
                    {
                        Pump(700);
                        using (Bitmap second = Capture(context.Preview.Bounds))
                        {
                            first.Save(Path.Combine(directory, "browser-covered-1.png"), ImageFormat.Png);
                            second.Save(Path.Combine(directory, "browser-covered-2.png"), ImageFormat.Png);
                            double difference = ChangedFraction(first, second);
                            lines.Add("浏览器被遮挡时帧变化比例：" + difference.ToString("P2"));
                            Check("浏览器被遮挡时实际视频画面持续更新", difference > .015);
                        }
                    }
                }
            }
            catch (Exception error) { failed++; lines.Add("FAIL " + error.Message); }
            lines.Add("结果：" + (failed == 0 ? "全部通过" : failed + " 项失败"));
            File.WriteAllLines(Path.Combine(directory, "browser-test.txt"), lines.ToArray(), new UTF8Encoding(true));
            return failed == 0 ? 0 : 1;
        }
        internal static int Run(string directory, bool visual)
        {
            Directory.CreateDirectory(directory); lines.Clear(); failed = 0;
            lines.Add("悬停视频预览验证报告"); lines.Add("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            lines.Add("模式：" + (visual ? "窗口与实时画面验证" : "逻辑、配置与接口结构验证"));
            try
            {
                Check("Windows 接口结构大小正确", Marshal.SizeOf(typeof(Native.ThumbnailProperties)) == 48);
                Check("Windows 接口布尔字段对齐正确", Marshal.OffsetOf(typeof(Native.ThumbnailProperties), "Visible").ToInt32() == 40);
                HoverGate gate = new HoverGate();
                Check("未悬停时保持隐藏", !gate.Update(false, true, 0, 250, 120));
                Check("短暂掠过不会弹出", !gate.Update(true, true, 100, 250, 120) && !gate.Update(false, true, 180, 250, 120));
                Check("重新进入会重新等待", !gate.Update(true, true, 200, 250, 120) && !gate.Update(true, true, 449, 250, 120));
                Check("达到等待时间后显示", gate.Update(true, true, 450, 250, 120));
                Check("移开后按设置延迟隐藏", gate.Update(false, true, 460, 250, 120) && !gate.Update(false, true, 580, 250, 120));
                gate.Update(true, true, 600, 0, 0);
                Check("暂停时立即隐藏并清除等待", !gate.Update(true, false, 610, 250, 120) && !gate.Update(true, true, 620, 250, 120));
                gate.Reset(); gate.Update(true, true, 0, 0, 0);
                Check("零延迟可立即隐藏", !gate.Update(false, true, 1, 0, 0));
                PreviewSettings settings = new PreviewSettings(); settings.CropLeft = .2; settings.CropTop = .1; settings.CropRight = .8; settings.CropBottom = .9;
                Rectangle crop = Geometry.Crop(1000, 500, settings);
                Check("视频区域裁剪坐标准确", crop == new Rectangle(200, 50, 600, 400));
                Check("保持宽高比而不拉伸", Geometry.Fit(new Size(1920, 1080), new Rectangle(0, 0, 320, 240)) == new Rectangle(0, 30, 320, 180));
                Rectangle placed = Geometry.Place(new Rectangle(-100, 900, 60, 20), new Size(360, 210), new Rectangle(-1920, 0, 1920, 1080));
                Check("负坐标副屏与屏幕边缘定位", new Rectangle(-1920, 0, 1920, 1080).Contains(placed) && placed.Bottom < 900);
                settings.CropLeft = Double.NaN; settings.PreviewWidth = 99999; settings.ShowDelay = -5; settings.Validate();
                Check("非法裁剪和参数会恢复有效值", settings.CropLeft == 0 && settings.CropRight == 1 && settings.PreviewWidth == 960 && settings.ShowDelay == 0);
                string configurationPath = Path.Combine(directory, "test-settings.xml");
                settings.LastWindowTitle = "验证用窗口"; settings.ShowMarker = false; settings.Save(configurationPath);
                PreviewSettings restored = PreviewSettings.Load(configurationPath);
                Check("配置可保存并重新读取", restored.LastWindowTitle == "验证用窗口" && !restored.ShowMarker && restored.PreviewWidth == 960);
                File.WriteAllText(Path.Combine(directory, "invalid-settings.xml"), "<broken>");
                Check("损坏配置可安全回到默认值", PreviewSettings.Load(Path.Combine(directory, "invalid-settings.xml")).PreviewWidth == 360);
                if (visual) VerifyWindows(directory);
            }
            catch (Exception error) { failed++; lines.Add("FAIL 未预期异常：" + error); }
            lines.Add("结果：" + (failed == 0 ? "全部通过" : failed + " 项失败"));
            File.WriteAllLines(Path.Combine(directory, visual ? "visual-test.txt" : "self-test.txt"), lines.ToArray(), new UTF8Encoding(true));
            return failed == 0 ? 0 : 1;
        }
        private static void Check(string name, bool passed)
        { lines.Add((passed ? "PASS " : "FAIL ") + name); if (!passed) failed++; }
        private static void Pump(int milliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(12); }
        }
        private static Bitmap Capture(Rectangle rectangle)
        {
            Native.DwmFlush();
            Bitmap bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
            return bitmap;
        }
        private static double ChangedFraction(Bitmap first, Bitmap second)
        {
            int changed = 0, total = 0;
            for (int y = 8; y < first.Height - 8; y += 4)
                for (int x = 8; x < first.Width - 8; x += 4)
                {
                    Color a = first.GetPixel(x, y), b = second.GetPixel(x, y);
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 35) changed++;
                    total++;
                }
            return total == 0 ? 0 : (double)changed / total;
        }
        private static void VerifyWindows(string directory)
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            using (AnimatedSource source = new AnimatedSource())
            using (QuietForm cover = new QuietForm())
            using (PreviewContext context = new PreviewContext(true))
            {
                source.Bounds = new Rectangle(work.Right - 510, work.Top + 100, 480, 270); source.Show();
                cover.FormBorderStyle = FormBorderStyle.None; cover.StartPosition = FormStartPosition.Manual;
                cover.Bounds = source.Bounds; cover.BackColor = Color.FromArgb(75, 81, 95);
                cover.Text = "预览验证专用遮挡窗口"; cover.ShowInTaskbar = false; cover.Show();
                context.Settings.SetHotArea(new Rectangle(work.Left + 40, work.Top + 28, 90, 24));
                context.Settings.PreviewWidth = 320; context.Settings.ShowDelay = 100; context.Settings.HideDelay = 80; context.Settings.ShowMarker = false;
                Point fakePointer = new Point(work.Left + 2, work.Bottom - 2); context.PointerProvider = delegate { return fakePointer; };
                uint pid = (uint)Process.GetCurrentProcess().Id;
                context.Start(new WindowItem(source.Handle, pid, source.Text, "test")); Pump(120);
                Check("实际窗口：没有悬停时隐藏", !context.Preview.Visible);
                IntPtr foreground = Native.GetForegroundWindow();
                fakePointer = new Point(context.Settings.HotX + 10, context.Settings.HotY + 10); context.Tick(); Pump(180);
                Check("实际窗口：悬停后自动弹出", context.Preview.Visible);
                Check("弹出预览不抢当前窗口焦点", Native.GetForegroundWindow() == foreground);
                using (Bitmap first = Capture(context.Preview.Bounds))
                {
                    first.Save(Path.Combine(directory, "preview-frame-1.png"), ImageFormat.Png); Pump(210);
                    using (Bitmap second = Capture(context.Preview.Bounds))
                    {
                        second.Save(Path.Combine(directory, "preview-frame-2.png"), ImageFormat.Png);
                        double difference = ChangedFraction(first, second);
                        lines.Add("被遮挡的源窗口：连续预览帧变化比例 = " + difference.ToString("P2"));
                        Check("源窗口被遮挡时画面仍实时更新", difference > .015);
                        Color center = second.GetPixel(second.Width / 2, second.Height / 2);
                        Check("预览显示源画面而非遮挡窗口", Math.Abs(center.B - 95) + Math.Abs(center.R - 75) > 40);
                    }
                }
                fakePointer = new Point(work.Left + 2, work.Bottom - 2); context.Tick(); Pump(150);
                Check("实际窗口：移开后自动隐藏", !context.Preview.Visible);
                fakePointer = new Point(context.Settings.HotX + 10, context.Settings.HotY + 10); context.Tick(); Pump(180);
                context.TogglePause(); Check("实际窗口：暂停会立即收起画面", !context.Preview.Visible); Pump(160);
                Check("暂停期间悬停不会重新弹出", !context.Preview.Visible);
                context.TogglePause(); Pump(180);
                Check("恢复后可再次悬停显示", context.Preview.Visible);
                context.Settings.CropLeft = .25; context.Settings.CropTop = .25; context.Settings.CropRight = .75; context.Settings.CropBottom = .75;
                context.Preview.UpdateLayout(); Pump(100);
                Check("实际预览支持局部裁剪", context.Preview.CurrentCrop.Width == 240 && context.Preview.CurrentCrop.Height >= 134 && context.Preview.CurrentCrop.Height <= 136);
                using (Bitmap crop = Capture(context.Preview.Bounds)) crop.Save(Path.Combine(directory, "preview-cropped.png"), ImageFormat.Png);
                Native.ShowWindow(source.Handle, 7); Pump(100);
                Check("源窗口最小化时收起冻结画面", !context.Preview.Visible);
                Native.ShowWindow(source.Handle, 4); Pump(220);
                Check("恢复源窗口后预览可继续显示", context.Preview.Visible);
                source.Close(); Pump(100);
                Check("源窗口关闭后安全停止", !context.Running && !context.Preview.Visible);
                using (SettingsForm settings = new SettingsForm(context))
                {
                    settings.StartPosition = FormStartPosition.Manual;
                    settings.Location = new Point(work.Left + 30, work.Top + 30);
                    Native.ShowWindow(settings.Handle, 4); Pump(220);
                    Check("设置界面：启动按钮无需滚动即可完整显示", settings.StartButtonFullyVisible);
                    using (Bitmap image = Capture(settings.Bounds)) image.Save(Path.Combine(directory, "settings-window.png"), ImageFormat.Png);
                    settings.ClientSize = new Size(650, 535); Pump(100);
                    Check("设置界面：缩小窗口后启动按钮仍完整显示", settings.StartButtonFullyVisible);
                    using (Bitmap image = Capture(settings.Bounds)) image.Save(Path.Combine(directory, "settings-window-small.png"), ImageFormat.Png);
                    settings.Hide();
                }
                Check("测试过程未移动真实鼠标", true);
            }
        }
    }

    internal sealed class AnimatedSource : QuietForm
    {
        private readonly System.Windows.Forms.Timer animation;
        private int frame;
        internal AnimatedSource()
        {
            Text = "实时画面验证 · 动画源"; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
            animation = new System.Windows.Forms.Timer(); animation.Interval = 30;
            animation.Tick += delegate { frame++; Invalidate(); }; animation.Start();
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            Color color = (frame / 5) % 2 == 0 ? Color.FromArgb(24, 123, 190) : Color.FromArgb(168, 58, 105);
            args.Graphics.Clear(color);
            using (Font font = new Font("Microsoft YaHei UI", 15f)) args.Graphics.DrawString("实时预览测试   帧 " + frame, font, Brushes.White, 22, 18);
            args.Graphics.FillEllipse(Brushes.White, 20 + (frame * 13) % 370, 100, 58, 58);
            using (Font font = new Font("Microsoft YaHei UI", 11f)) args.Graphics.DrawString("这是测试动画窗口，不是用户的视频。", font, Brushes.White, 22, 218);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { animation.Stop(); animation.Dispose(); } base.Dispose(disposing); }
    }
}
