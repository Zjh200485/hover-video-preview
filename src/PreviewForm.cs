using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace HoverVideoPreview
{
    internal class QuietForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE; return p; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
    }

    internal sealed class PreviewForm : QuietForm
    {
        private IntPtr thumbnail = IntPtr.Zero, source = IntPtr.Zero;
        private Bitmap browserImage;
        private long browserSequence = -1;
        internal bool BrowserMode { get; private set; }
        internal PreviewSettings Settings;
        internal Rectangle CurrentCrop { get; private set; }
        internal Rectangle CurrentDestination { get; private set; }
        internal IntPtr Source { get { return source; } }
        internal PreviewForm(PreviewSettings settings)
        {
            Settings = settings; Text = "悬停视频预览 · 画面"; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; BackColor = Color.FromArgb(24, 24, 24);
            StartPosition = FormStartPosition.Manual; ClientSize = new Size(360, 210);
        }
        internal void Attach(IntPtr hwnd)
        {
            Detach();
            BrowserMode = false;
            if (!Native.IsWindow(hwnd)) throw new InvalidOperationException("所选窗口已经关闭，请刷新后重新选择。");
            bool enabled;
            if (Native.DwmIsCompositionEnabled(out enabled) < 0 || !enabled)
                throw new InvalidOperationException("Windows 当前没有开启桌面合成，无法显示实时窗口预览。");
            int error = Native.DwmRegisterThumbnail(Handle, hwnd, out thumbnail);
            if (error < 0) { thumbnail = IntPtr.Zero; throw new InvalidOperationException("无法连接这个窗口。错误码：0x" + error.ToString("X8")); }
            source = hwnd;
            try { UpdateLayout(); } catch { Detach(); throw; }
        }
        internal void UpdateLayout()
        {
            Native.SIZE size;
            int error = 0;
            if (BrowserMode)
            { if (browserImage == null) return; size = new Native.SIZE(); size.Width = browserImage.Width; size.Height = browserImage.Height; }
            else { if (thumbnail == IntPtr.Zero) return; error = Native.DwmQueryThumbnailSourceSize(thumbnail, out size); }
            if (error < 0 || size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("暂时无法读取窗口画面。请恢复视频窗口后重试。");
            CurrentCrop = Geometry.Crop(size.Width, size.Height, Settings);
            Rectangle work = Screen.FromPoint(new Point(Settings.HotX, Settings.HotY)).WorkingArea;
            int width = Math.Min(Settings.PreviewWidth, work.Width - 12);
            int height = Math.Max(70, (int)Math.Round((double)(width - 2) * CurrentCrop.Height / CurrentCrop.Width) + 2);
            height = Math.Min(height, Math.Min(700, work.Height - 12));
            Bounds = Geometry.Place(Settings.HotArea, new Size(width, height), work);
            CurrentDestination = Geometry.Fit(CurrentCrop.Size, new Rectangle(1, 1, ClientSize.Width - 2, ClientSize.Height - 2));
            if (BrowserMode) { Invalidate(); return; }
            Native.ThumbnailProperties props = new Native.ThumbnailProperties();
            props.Flags = 0x01 | 0x02 | 0x04 | 0x08 | 0x10;
            props.Destination = new Native.RECT(CurrentDestination); props.Source = new Native.RECT(CurrentCrop);
            props.Opacity = 255; props.Visible = true; props.SourceClientAreaOnly = false;
            error = Native.DwmUpdateThumbnailProperties(thumbnail, ref props);
            if (error < 0) throw new InvalidOperationException("更新预览画面失败。错误码：0x" + error.ToString("X8"));
        }
        internal void ShowQuietly()
        {
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        internal void Detach()
        {
            Hide();
            if (thumbnail != IntPtr.Zero) { Native.DwmUnregisterThumbnail(thumbnail); thumbnail = IntPtr.Zero; }
            source = IntPtr.Zero;
            if (browserImage != null) { browserImage.Dispose(); browserImage = null; }
            browserSequence = -1; BrowserMode = false;
        }
        internal void UseBrowser()
        { Detach(); BrowserMode = true; DoubleBuffered = true; }
        internal void SetBrowserFrame(BrowserFrame frame)
        {
            if (!BrowserMode || frame == null || frame.Sequence == browserSequence) return;
            Bitmap replacement;
            using (MemoryStream stream = new MemoryStream(frame.Bytes))
            using (Image image = Image.FromStream(stream)) replacement = new Bitmap(image);
            Bitmap old = browserImage; browserImage = replacement; browserSequence = frame.Sequence;
            if (old != null) old.Dispose(); UpdateLayout(); Invalidate();
        }
        internal Bitmap CopyBrowserImage() { return browserImage == null ? null : new Bitmap(browserImage); }
        protected override void Dispose(bool disposing) { Detach(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (BrowserMode && browserImage != null)
            { args.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear; args.Graphics.DrawImage(browserImage, CurrentDestination, CurrentCrop, GraphicsUnit.Pixel); }
            using (Pen pen = new Pen(Color.FromArgb(90, 90, 90))) args.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    internal sealed class MarkerForm : QuietForm
    {
        internal event EventHandler SettingsRequested;
        internal MarkerForm()
        {
            Text = "悬停视频预览 · 触发区"; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; BackColor = Color.FromArgb(232, 235, 240);
            ForeColor = Color.FromArgb(75, 83, 95); StartPosition = FormStartPosition.Manual;
            Font = new Font("Microsoft YaHei UI", 9f); Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            TextRenderer.DrawText(args.Graphics, "预览", Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (Pen pen = new Pen(Color.FromArgb(174, 181, 192))) args.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        protected override void OnMouseDown(MouseEventArgs args)
        { if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty); base.OnMouseDown(args); }
    }

    internal sealed class ScreenSelector : Form
    {
        private Point start, current;
        private bool dragging;
        private readonly string instruction;
        internal Rectangle Selection { get; private set; }
        internal ScreenSelector(Rectangle bounds, string text)
        {
            instruction = text; Bounds = bounds; StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Color.Black; Opacity = 0.38; Cursor = Cursors.Cross; KeyPreview = true;
            DoubleBuffered = true; Font = new Font("Microsoft YaHei UI", 16f);
        }
        protected override void OnMouseDown(MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            start = current = args.Location; dragging = true; Capture = true; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs args) { if (dragging) { current = args.Location; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs args)
        {
            if (!dragging || args.Button != MouseButtons.Left) return;
            current = args.Location; Capture = false; dragging = false;
            Rectangle r = Rectangle.FromLTRB(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y), Math.Max(start.X, current.X), Math.Max(start.Y, current.Y));
            r = Rectangle.Intersect(ClientRectangle, r);
            if (r.Width < 12 || r.Height < 10) { Invalidate(); return; }
            Selection = new Rectangle(r.Left + Left, r.Top + Top, r.Width, r.Height); DialogResult = DialogResult.OK; Close();
        }
        protected override void OnKeyDown(KeyEventArgs args) { if (args.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } base.OnKeyDown(args); }
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            args.Graphics.DrawString(instruction + "\n按住左键拖动框选，松开确认；Esc 取消。", Font, Brushes.White, 24, 24);
            if (dragging)
            {
                Rectangle r = Rectangle.FromLTRB(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y), Math.Max(start.X, current.X), Math.Max(start.Y, current.Y));
                using (Pen pen = new Pen(Color.White, 4)) args.Graphics.DrawRectangle(pen, r);
            }
        }
    }
}
