using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace HoverVideoPreview
{
    public sealed class PreviewSettings
    {
        public int HotX = 80, HotY = 12, HotWidth = 100, HotHeight = 26;
        public int PreviewWidth = 360, ShowDelay = 250, HideDelay = 120;
        public bool ShowMarker = true, KeepOnPreview = false;
        public bool BrowserMode = false;
        public double CropLeft = 0, CropTop = 0, CropRight = 1, CropBottom = 1;
        public string LastWindowTitle = "", LastProcessName = "";
        [XmlIgnore] public Rectangle HotArea { get { return new Rectangle(HotX, HotY, HotWidth, HotHeight); } }
        public void SetHotArea(Rectangle r) { HotX = r.X; HotY = r.Y; HotWidth = r.Width; HotHeight = r.Height; }
        public void ResetCrop() { CropLeft = CropTop = 0; CropRight = CropBottom = 1; }
        public void Validate()
        {
            HotWidth = Geometry.Clamp(HotWidth, 12, 1200);
            HotHeight = Geometry.Clamp(HotHeight, 10, 600);
            PreviewWidth = Geometry.Clamp(PreviewWidth, 160, 960);
            ShowDelay = Geometry.Clamp(ShowDelay, 0, 1500);
            HideDelay = Geometry.Clamp(HideDelay, 0, 1000);
            if (!Geometry.ValidCrop(CropLeft, CropTop, CropRight, CropBottom)) ResetCrop();
            bool onScreen = false;
            foreach (Screen screen in Screen.AllScreens)
                if (Rectangle.Intersect(screen.WorkingArea, HotArea).Width >= 12 && Rectangle.Intersect(screen.WorkingArea, HotArea).Height >= 10) onScreen = true;
            if (!onScreen) SetHotArea(new Rectangle(Screen.PrimaryScreen.WorkingArea.Left + 80, Screen.PrimaryScreen.WorkingArea.Top + 12, 100, 26));
        }
        public static PreviewSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    XmlReaderSettings options = new XmlReaderSettings(); options.DtdProcessing = DtdProcessing.Prohibit; options.XmlResolver = null;
                    using (XmlReader reader = XmlReader.Create(path, options))
                    {
                        PreviewSettings value = (PreviewSettings)new XmlSerializer(typeof(PreviewSettings)).Deserialize(reader);
                        value.Validate(); return value;
                    }
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (XmlException) { }
            catch (UnauthorizedAccessException) { }
            PreviewSettings fresh = new PreviewSettings(); fresh.Validate(); return fresh;
        }
        public void Save(string path)
        {
            string temporary = path + ".tmp";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(PreviewSettings)).Serialize(stream, this);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }

    internal static class Geometry
    {
        internal static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }
        internal static bool ValidCrop(double left, double top, double right, double bottom)
        {
            return !Double.IsNaN(left + top + right + bottom) && !Double.IsInfinity(left + top + right + bottom)
                && left >= 0 && top >= 0 && right <= 1 && bottom <= 1 && right - left >= 0.005 && bottom - top >= 0.005;
        }
        internal static Rectangle Crop(int width, int height, PreviewSettings settings)
        {
            int left = Clamp((int)Math.Floor(width * settings.CropLeft), 0, width - 1);
            int top = Clamp((int)Math.Floor(height * settings.CropTop), 0, height - 1);
            int right = Clamp((int)Math.Ceiling(width * settings.CropRight), left + 1, width);
            int bottom = Clamp((int)Math.Ceiling(height * settings.CropBottom), top + 1, height);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
        internal static Rectangle Fit(Size source, Rectangle destination)
        {
            double scale = Math.Min((double)destination.Width / source.Width, (double)destination.Height / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            return new Rectangle(destination.Left + (destination.Width - width) / 2, destination.Top + (destination.Height - height) / 2, width, height);
        }
        internal static Rectangle Place(Rectangle hotArea, Size size, Rectangle work)
        {
            int width = Math.Min(size.Width, work.Width - 12), height = Math.Min(size.Height, work.Height - 12);
            int x = Clamp(hotArea.Left, work.Left + 6, work.Right - width - 6);
            int y = hotArea.Bottom + 6;
            if (y + height > work.Bottom - 6) y = hotArea.Top - height - 6;
            y = Clamp(y, work.Top + 6, work.Bottom - height - 6);
            return new Rectangle(x, y, width, height);
        }
    }

    internal sealed class HoverGate
    {
        private long enteredAt = -1, leftAt = -1;
        internal bool Visible { get; private set; }
        internal bool Update(bool hovering, bool enabled, long now, int showDelay, int hideDelay)
        {
            if (!enabled) { Reset(); return false; }
            if (hovering)
            {
                leftAt = -1;
                if (enteredAt < 0) enteredAt = now;
                if (now - enteredAt >= showDelay) Visible = true;
            }
            else
            {
                enteredAt = -1;
                if (leftAt < 0) leftAt = now;
                if (now - leftAt >= hideDelay) Visible = false;
            }
            return Visible;
        }
        internal void Reset() { enteredAt = leftAt = -1; Visible = false; }
    }
}
