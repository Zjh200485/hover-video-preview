using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HoverVideoPreview
{
    internal sealed class SettingsForm : Form
    {
        private readonly PreviewContext context;
        private readonly ComboBox windows = new ComboBox();
        private readonly ComboBox mode = new ComboBox();
        private readonly TextBox connection = new TextBox();
        private readonly Label modeHelp = new Label();
        private readonly Button refreshButton;
        private readonly Label hotLabel = new Label(), cropLabel = new Label(), statusLabel = new Label();
        private readonly NumericUpDown width = new NumericUpDown(), delay = new NumericUpDown(), hideDelay = new NumericUpDown();
        private readonly CheckBox marker = new CheckBox(), stay = new CheckBox();
        private readonly Button startButton;
        private bool syncing;
        private IntPtr cropTarget = IntPtr.Zero;
        internal SettingsForm(PreviewContext controller)
        {
            context = controller; Text = "悬停视频预览 · 设置";
            Font = new Font("Microsoft YaHei UI", 9.5f); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(710, 690); MinimumSize = new Size(670, 570); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(247, 249, 252);
            TableLayoutPanel shell = new TableLayoutPanel(); shell.Dock = DockStyle.Fill; shell.ColumnCount = 1; shell.RowCount = 2;
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(shell);
            Panel scroll = new Panel(); scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true; scroll.Margin = Padding.Empty; shell.Controls.Add(scroll, 0, 0);
            TableLayoutPanel rows = new TableLayoutPanel(); rows.Dock = DockStyle.Top; rows.AutoSize = true;
            rows.Padding = new Padding(20); rows.ColumnCount = 1; rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); scroll.Controls.Add(rows);
            Add(rows, TextLabel("悬停显示，移开隐藏", 17f, Color.FromArgb(28, 47, 76)));
            Add(rows, TextLabel("视频后台播放时，也能在鼠标悬停处观看。", 10f, Color.FromArgb(91, 101, 118)));
            mode.DropDownStyle = ComboBoxStyle.DropDownList; mode.Width = 610;
            mode.Items.Add("浏览器后台模式：可切换标签页，也可最小化浏览器");
            mode.Items.Add("窗口预览模式：视频需要留在独立窗口的当前标签页");
            Add(rows, mode);
            connection.ReadOnly = true; connection.Width = 455;
            connection.Text = context.Bridge == null ? "后台接收器未启动" : context.Bridge.ConnectionCode;
            Add(rows, Flow(SmallLabel("连接码"), connection, Button("复制", delegate { if (context.Bridge != null) Clipboard.SetText(context.Bridge.ConnectionCode); })));
            Add(rows, TextLabel("① 选择播放视频的窗口", 11f, Color.FromArgb(28, 47, 76)));
            windows.DropDownStyle = ComboBoxStyle.DropDownList; windows.Width = 520; windows.DropDownWidth = 900;
            refreshButton = Button("刷新", delegate { RefreshWindows(); });
            Add(rows, Flow(windows, refreshButton));
            windows.SelectedIndexChanged += delegate
            {
                WindowItem item = windows.SelectedItem as WindowItem;
                if (!syncing && item != null && item.Handle != cropTarget)
                { context.Settings.ResetCrop(); cropTarget = item.Handle; UpdateLabels(); }
            };
            modeHelp.AutoSize = true; modeHelp.MaximumSize = new Size(640, 0); modeHelp.ForeColor = Color.FromArgb(91, 101, 118); Add(rows, modeHelp);
            Add(rows, TextLabel("② 设置显示区域和触发位置", 11f, Color.FromArgb(28, 47, 76)));
            Add(rows, Flow(Button("框选视频区域", CropVideo), Button("显示整个窗口", delegate { context.Settings.ResetCrop(); UpdateLabels(); }), Button("框选鼠标触发区", PickHotArea)));
            hotLabel.AutoSize = cropLabel.AutoSize = true; hotLabel.Margin = cropLabel.Margin = new Padding(3, 3, 3, 3);
            Add(rows, hotLabel); Add(rows, cropLabel);
            marker.Text = "显示触发区标记（熟悉位置后可取消）"; marker.AutoSize = true; Add(rows, marker);
            stay.Text = "鼠标移入预览画面时也保持显示"; stay.AutoSize = true; Add(rows, stay);
            width.Minimum = 160; width.Maximum = 960; width.Increment = 20; width.Width = 85;
            delay.Minimum = 0; delay.Maximum = 1500; delay.Increment = 50; delay.Width = 85;
            hideDelay.Minimum = 0; hideDelay.Maximum = 1000; hideDelay.Increment = 40; hideDelay.Width = 85;
            Add(rows, Flow(SmallLabel("预览宽度"), width, SmallLabel("像素    显示等待"), delay, SmallLabel("毫秒")));
            Add(rows, Flow(SmallLabel("移开后等待"), hideDelay, SmallLabel("毫秒隐藏；设为 0 可立即隐藏。")));
            TableLayoutPanel footer = new TableLayoutPanel(); footer.Dock = DockStyle.Fill; footer.AutoSize = true; footer.ColumnCount = 1;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.Padding = new Padding(20, 6, 20, 12);
            footer.Margin = Padding.Empty; footer.BackColor = Color.FromArgb(235, 241, 248); shell.Controls.Add(footer, 0, 1);
            startButton = Button("启动 / 应用设置", StartPreview); startButton.Name = "startPreview";
            Add(footer, Flow(startButton, Button("暂停 / 恢复", delegate { context.TogglePause(); }), Button("退出工具", delegate { context.ExitThread(); })));
            statusLabel.AutoSize = true; statusLabel.MaximumSize = new Size(640, 0); statusLabel.ForeColor = Color.FromArgb(30, 99, 74); Add(footer, statusLabel);
            Add(footer, TextLabel("Ctrl+Alt+P：暂停 / 恢复    Ctrl+Alt+O：打开设置\n启动后设置窗口会收起，可从右下角托盘再次打开。", 9f, Color.FromArgb(91, 101, 118)));
            context.StatusChanged += OnStatusChanged;
            mode.SelectedIndexChanged += delegate
            {
                if (!syncing) { context.Settings.ResetCrop(); UpdateLabels(); }
                UpdateMode();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (!context.Exiting && args.CloseReason == CloseReason.UserClosing)
                { args.Cancel = true; Hide(); context.SettingsHidden(); }
            };
            Sync();
            Shown += delegate
            {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                if (Height > work.Height - 24) Height = Math.Max(MinimumSize.Height, work.Height - 24);
                if (Width > work.Width - 24) Width = Math.Max(MinimumSize.Width, work.Width - 24);
                if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            };
        }
        internal bool StartButtonFullyVisible
        {
            get
            {
                Rectangle bounds = startButton.RectangleToScreen(startButton.ClientRectangle);
                for (Control parent = startButton.Parent; parent != null; parent = parent.Parent)
                    if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds)) return false;
                return Visible && startButton.Visible;
            }
        }
        private static Label TextLabel(string text, float size, Color color)
        {
            Label result = new Label(); result.Text = text; result.AutoSize = true; result.MaximumSize = new Size(645, 0);
            result.Font = new Font("Microsoft YaHei UI", size); result.ForeColor = color; result.Margin = new Padding(3, 7, 3, 6); return result;
        }
        private static Label SmallLabel(string text)
        { Label label = new Label(); label.AutoSize = true; label.Text = text; label.Margin = new Padding(3, 7, 3, 3); return label; }
        private static Button Button(string text, Action action)
        {
            Button button = new Button(); button.Text = text; button.AutoSize = true; button.Padding = new Padding(6, 3, 6, 3);
            button.Click += delegate { action(); }; return button;
        }
        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            FlowLayoutPanel result = new FlowLayoutPanel(); result.AutoSize = true; result.WrapContents = true;
            result.Dock = DockStyle.Fill; result.Margin = new Padding(0, 1, 0, 2);
            foreach (Control control in controls) result.Controls.Add(control); return result;
        }
        private static void Add(TableLayoutPanel rows, Control control)
        { int row = rows.RowCount++; rows.RowStyles.Add(new RowStyle(SizeType.AutoSize)); rows.Controls.Add(control, 0, row); }
        internal void Sync()
        {
            syncing = true;
            mode.SelectedIndex = context.Settings.BrowserMode ? 0 : 1; UpdateMode();
            width.Value = context.Settings.PreviewWidth; delay.Value = context.Settings.ShowDelay; hideDelay.Value = context.Settings.HideDelay;
            marker.Checked = context.Settings.ShowMarker; stay.Checked = context.Settings.KeepOnPreview;
            RefreshWindows(); UpdateLabels(); statusLabel.Text = context.Status; syncing = false;
        }
        private void RefreshWindows()
        {
            bool wasSyncing = syncing; syncing = true;
            WindowItem old = windows.SelectedItem as WindowItem;
            List<WindowItem> items = Native.ListWindows(); windows.Items.Clear();
            foreach (WindowItem item in items) windows.Items.Add(item);
            int selected = -1;
            for (int i = 0; i < items.Count; i++)
                if ((old != null && items[i].Handle == old.Handle) || (old == null && items[i].Handle == context.Preview.Source)) { selected = i; break; }
            if (selected < 0)
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Title == context.Settings.LastWindowTitle && items[i].ProcessName == context.Settings.LastProcessName) { selected = i; break; }
            if (selected >= 0) { windows.SelectedIndex = selected; cropTarget = items[selected].Handle; }
            syncing = wasSyncing;
        }
        private void UpdateLabels()
        {
            PreviewSettings s = context.Settings;
            hotLabel.Text = "触发区：(" + s.HotX + ", " + s.HotY + ")   " + s.HotWidth + " × " + s.HotHeight + " 像素";
            cropLabel.Text = (s.CropLeft == 0 && s.CropTop == 0 && s.CropRight == 1 && s.CropBottom == 1)
                ? "预览内容：完整画面（后台模式会优先识别视频区域）" : "预览内容：已框选区域（调整布局后请重新框选）";
        }
        private void UpdateMode()
        {
            bool browser = mode.SelectedIndex == 0; windows.Enabled = refreshButton.Enabled = !browser;
            connection.Enabled = browser;
            modeHelp.Text = browser
                ? "复制连接码，在视频标签页中打开配套扩展并点击“开始后台预览”。此模式无需选择窗口。"
                : "视频应是所选窗口当前显示的标签页，保持窗口未最小化。";
        }
        private void ReadControls()
        {
            context.Settings.PreviewWidth = (int)width.Value; context.Settings.ShowDelay = (int)delay.Value; context.Settings.HideDelay = (int)hideDelay.Value;
            context.Settings.ShowMarker = marker.Checked; context.Settings.KeepOnPreview = stay.Checked; context.Settings.Validate();
            context.Settings.BrowserMode = mode.SelectedIndex == 0;
        }
        private void StartPreview()
        {
            try
            {
                ReadControls();
                if (context.Settings.BrowserMode) context.StartBrowser(); else context.Start(windows.SelectedItem as WindowItem);
                Hide(); context.SettingsHidden();
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "暂时无法启动", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private void PickHotArea()
        {
            ReadControls(); context.Suppress = true; context.HideTransient(); Hide();
            try
            {
                using (ScreenSelector selector = new ScreenSelector(SystemInformation.VirtualScreen, "选择鼠标悬停触发的位置"))
                    if (selector.ShowDialog() == DialogResult.OK) context.Settings.SetHotArea(selector.Selection);
            }
            finally { context.Suppress = false; Show(); Activate(); UpdateLabels(); }
        }
        private void CropVideo()
        {
            if (mode.SelectedIndex == 0) { CropBrowserImage(); return; }
            WindowItem item = windows.SelectedItem as WindowItem;
            if (item == null || !Native.IsWindow(item.Handle)) { MessageBox.Show(this, "请先选择播放视频的窗口。", "框选视频区域"); return; }
            ReadControls(); context.Suppress = true; context.HideTransient(); Hide();
            try
            {
                if (Native.IsIconic(item.Handle)) Native.ShowWindow(item.Handle, 9);
                Native.SetForegroundWindow(item.Handle);
                Native.RECT bounds;
                if (!Native.GetWindowRect(item.Handle, out bounds)) throw new InvalidOperationException("无法读取窗口位置。");
                Rectangle visible = Rectangle.Intersect(bounds.ToRectangle(), SystemInformation.VirtualScreen);
                if (visible.Width < 20 || visible.Height < 20) throw new InvalidOperationException("请先把视频窗口移回屏幕内。");
                using (ScreenSelector selector = new ScreenSelector(visible, "在视频画面上框选要预览的区域"))
                {
                    if (selector.ShowDialog() == DialogResult.OK)
                    {
                        Rectangle selected = selector.Selection;
                        context.Settings.CropLeft = (double)(selected.Left - bounds.Left) / (bounds.Right - bounds.Left);
                        context.Settings.CropTop = (double)(selected.Top - bounds.Top) / (bounds.Bottom - bounds.Top);
                        context.Settings.CropRight = (double)(selected.Right - bounds.Left) / (bounds.Right - bounds.Left);
                        context.Settings.CropBottom = (double)(selected.Bottom - bounds.Top) / (bounds.Bottom - bounds.Top);
                        cropTarget = item.Handle; context.Settings.Validate();
                    }
                }
            }
            catch (Exception error) { MessageBox.Show(error.Message, "框选视频区域"); }
            finally { context.Suppress = false; Show(); Activate(); UpdateLabels(); }
        }
        private void CropBrowserImage()
        {
            BrowserFrame frame = context.Bridge == null ? null : context.Bridge.Latest;
            if (frame == null) { MessageBox.Show(this, "请先在视频标签页打开扩展，连接并开始后台预览，再框选画面。", "框选视频区域"); return; }
            ReadControls(); context.Suppress = true; context.HideTransient(); Hide();
            try
            {
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream(frame.Bytes))
                using (Image image = Image.FromStream(stream))
                using (Form viewer = new Form())
                {
                    viewer.Text = "后台视频区域 · 准备框选"; viewer.FormBorderStyle = FormBorderStyle.None; viewer.TopMost = true; viewer.ShowInTaskbar = false;
                    Rectangle work = Screen.PrimaryScreen.WorkingArea;
                    Rectangle display = Geometry.Fit(image.Size, new Rectangle(work.Left + 24, work.Top + 24, Math.Min(1000, work.Width - 48), Math.Min(700, work.Height - 48)));
                    viewer.StartPosition = FormStartPosition.Manual; viewer.Bounds = display;
                    viewer.BackgroundImage = image; viewer.BackgroundImageLayout = ImageLayout.Stretch; viewer.Show();
                    using (ScreenSelector selector = new ScreenSelector(display, "框选后台视频中要观看的区域"))
                        if (selector.ShowDialog() == DialogResult.OK)
                        {
                            Rectangle selected = selector.Selection;
                            context.Settings.CropLeft = (double)(selected.Left - display.Left) / display.Width;
                            context.Settings.CropTop = (double)(selected.Top - display.Top) / display.Height;
                            context.Settings.CropRight = (double)(selected.Right - display.Left) / display.Width;
                            context.Settings.CropBottom = (double)(selected.Bottom - display.Top) / display.Height;
                        }
                    viewer.BackgroundImage = null;
                }
            }
            finally { context.Suppress = false; Show(); Activate(); UpdateLabels(); }
        }
        private void OnStatusChanged(object sender, EventArgs args) { if (!IsDisposed) statusLabel.Text = context.Status; }
        protected override void Dispose(bool disposing)
        { if (disposing) context.StatusChanged -= OnStatusChanged; base.Dispose(disposing); }
    }
}
