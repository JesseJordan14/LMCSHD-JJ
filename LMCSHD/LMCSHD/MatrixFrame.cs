using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static LMCSHD.PixelOrder;

namespace LMCSHD
{
    public static class MatrixFrame
    {
        #region Public_Variables
        //implmented get/set to expose references
        public static int Width { get; set; } = 16;
        public static int Height { get; set; } = 16;
        //public static System.Windows.Media.Color[] GradientColors { get; set; } = new System.Windows.Media.Color[2];
        public static Pixel[] Frame { get; set; }
        public static int FrameByteCount { get { return (Width * Height * 3); } }
        public static Orientation orientation { get; set; } = Orientation.HZ;
        public static StartCorner startCorner { get; set; } = StartCorner.TL;
        public static NewLine newLine { get; set; } = NewLine.SC;

        // When Sections is non-empty, GetOrderedSerialFrame walks each section
        // in its own local serpentine order instead of treating the matrix as one panel.
        // Empty list = backward-compatible single-panel behavior.
        public static List<Section> Sections { get; set; } = new List<Section>();

        // Temp scaffolding for multi-panel milestone 1.1: auto-configure 2 sections
        // when matrix is 32x32. Remove once a real Sections UI exists.
        public static bool UseTestSectionsOn32x32 = true;

        // Output transform: brightness scaling + gamma correction applied per-channel
        // in EmitRegion via _lut. Defaults are no-op (Brightness=1, Gamma=1).
        // Only EmitRegion consumes _lut — AppendRegionCoords doesn't, since coords
        // carry no color data.
        public const float BrightnessDefault = 1.0f;
        public const float GammaDefault = 1.0f;
        private static float _brightness = BrightnessDefault;
        public static float Brightness
        {
            get { return _brightness; }
            set
            {
                float v = value < 0f ? 0f : (value > 1f ? 1f : value);
                if (v != _brightness) { _brightness = v; RebuildLut(); }
            }
        }
        private static float _gamma = GammaDefault;
        public static float Gamma
        {
            get { return _gamma; }
            set
            {
                float v = value < 0.1f ? 0.1f : (value > 5.0f ? 5.0f : value);
                if (v != _gamma) { _gamma = v; RebuildLut(); }
            }
        }
        private static byte[] _lut = BuildIdentityLut();

        private static byte[] BuildIdentityLut()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = (byte)i;
            return t;
        }
        private static void RebuildLut()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double scaled = (i / 255.0) * _brightness;
                double curved = Math.Pow(scaled, _gamma);
                int v = (int)Math.Round(curved * 255.0);
                if (v < 0) v = 0; else if (v > 255) v = 255;
                t[i] = (byte)v;
            }
            _lut = t;
        }
        #endregion


        public delegate void FrameChangedEventHandler();
        public static event FrameChangedEventHandler FrameChanged;
        private static void OnFrameChanged() { FrameChanged?.Invoke(); }

        public delegate void DimensionsChangedEventHandler();
        public static event DimensionsChangedEventHandler DimensionsChanged;
        private static void OnDimensionsChanged() { DimensionsChanged?.Invoke(); }


        public static void SetDimensions(int w, int h)
        {
            bool dimensionsChanged = (w != Width || h != Height);
            Width = w;
            Height = h;
            Frame = null;
            Frame = new Pixel[Width * Height];

            // Sections may now extend past the new matrix size; safer to drop them.
            if (dimensionsChanged) Sections.Clear();

            // Restore sections saved for these dimensions, if any.
            if (Sections.Count == 0) LoadSections();

            // First-run scaffolding: only if nothing was loaded.
            if (UseTestSectionsOn32x32 && w == 32 && h == 32 && Sections.Count == 0)
                UseTestSections_TwoPanels32x32();

            OnDimensionsChanged();
        }

        // Persistence: sections are saved per matrix size to %LOCALAPPDATA%\LMCSHD-JJ\sections-WxH.dat.
        // Format: one section per line, "X Y W H Orientation StartCorner NewLine" — space separated.
        public static void SaveSections()
        {
            try
            {
                string path = SectionsFilePath(Width, Height);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = new StreamWriter(path, false))
                {
                    foreach (var s in Sections)
                        writer.WriteLine(s.X + " " + s.Y + " " + s.Width + " " + s.Height + " " +
                                         s.Orientation + " " + s.StartCorner + " " + s.NewLine);
                }
            }
            catch { /* best-effort: silent failure preserves in-memory state */ }
        }

        public static void LoadSections()
        {
            string path = SectionsFilePath(Width, Height);
            if (!File.Exists(path)) return;
            try
            {
                var loaded = new List<Section>();
                foreach (var line in File.ReadAllLines(path))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length != 7) continue;
                    int x, y, w, h;
                    Orientation orient;
                    StartCorner sc;
                    NewLine nl;
                    if (!int.TryParse(p[0], out x)) continue;
                    if (!int.TryParse(p[1], out y)) continue;
                    if (!int.TryParse(p[2], out w)) continue;
                    if (!int.TryParse(p[3], out h)) continue;
                    if (!Enum.TryParse(p[4], out orient)) continue;
                    if (!Enum.TryParse(p[5], out sc)) continue;
                    if (!Enum.TryParse(p[6], out nl)) continue;
                    loaded.Add(new Section(x, y, w, h, orient, sc, nl));
                }
                Sections = loaded;
            }
            catch { /* ignore corrupt file; fall back to scaffolding */ }
        }

        private static string SectionsFilePath(int w, int h)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LMCSHD-JJ");
            return Path.Combine(dir, "sections-" + w + "x" + h + ".dat");
        }

        // Display prefs: brightness + gamma. One key=value pair per line, invariant culture.
        // Single global file (not per-dimension) — these are user-display preferences, not matrix layout.
        public static void SaveDisplayPrefs()
        {
            try
            {
                string path = DisplayPrefsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                using (var writer = new StreamWriter(path, false))
                {
                    writer.WriteLine("brightness " + _brightness.ToString(ic));
                    writer.WriteLine("gamma " + _gamma.ToString(ic));
                }
            }
            catch { /* best-effort */ }
        }

        public static void LoadDisplayPrefs()
        {
            string path = DisplayPrefsFilePath();
            if (!File.Exists(path)) return;
            try
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var line in File.ReadAllLines(path))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length != 2) continue;
                    float v;
                    if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, ic, out v)) continue;
                    if (p[0] == "brightness") Brightness = v;
                    else if (p[0] == "gamma") Gamma = v;
                }
            }
            catch { /* ignore corrupt file */ }
        }

        private static string DisplayPrefsFilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LMCSHD-JJ");
            return Path.Combine(dir, "display-prefs.dat");
        }

        // Configures Sections for the author's 2x (16x32) wall.
        // Each section uses BR/HZ/SN, matching the wiring proven out by the
        // single-panel test in the LED_Display firmware repo.
        public static void UseTestSections_TwoPanels32x32()
        {
            Sections = new List<Section>
            {
                new Section(0,  0, 16, 32, Orientation.HZ, StartCorner.BR, NewLine.SN),
                new Section(16, 0, 16, 32, Orientation.HZ, StartCorner.BR, NewLine.SN),
            };
        }

        public static void Refresh()
        {
            OnFrameChanged();
        }

        public static byte[] GetOrderedSerialFrame()
        {
            if (Sections == null || Sections.Count == 0)
            {
                byte[] buf = new byte[Width * Height * 3];
                EmitRegion(buf, 0, new Section(0, 0, Width, Height, orientation, startCorner, newLine));
                return buf;
            }

            int totalPixels = 0;
            foreach (var s in Sections) totalPixels += s.PixelCount;
            byte[] outBuf = new byte[totalPixels * 3];
            int offset = 0;
            foreach (var s in Sections)
                offset = EmitRegion(outBuf, offset, s);
            return outBuf;
        }

        // Returns each LED's (x, y) in the order it appears in the LED chain,
        // accounting for sections / global serpentine. Used by test patterns
        // (e.g. walking pixel) to step through LEDs in physical chain order.
        public static List<Point> GetChainOrderCoords()
        {
            var list = new List<Point>(Width * Height);
            if (Sections == null || Sections.Count == 0)
                AppendRegionCoords(list, new Section(0, 0, Width, Height, orientation, startCorner, newLine));
            else
                foreach (var s in Sections)
                    AppendRegionCoords(list, s);
            return list;
        }

        private static void AppendRegionCoords(List<Point> list, Section s)
        {
            int startX = (s.StartCorner == StartCorner.TR || s.StartCorner == StartCorner.BR) ? s.Width - 1 : 0;
            int termX  = (s.StartCorner == StartCorner.TR || s.StartCorner == StartCorner.BR) ? -1 : s.Width;
            int incX   = (s.StartCorner == StartCorner.TR || s.StartCorner == StartCorner.BR) ? -1 : 1;
            int startY = (s.StartCorner == StartCorner.BL || s.StartCorner == StartCorner.BR) ? s.Height - 1 : 0;
            int termY  = (s.StartCorner == StartCorner.BL || s.StartCorner == StartCorner.BR) ? -1 : s.Height;
            int incY   = (s.StartCorner == StartCorner.BL || s.StartCorner == StartCorner.BR) ? -1 : 1;

            if (s.Orientation == Orientation.HZ)
            {
                for (int y = startY; y != termY; y += incY)
                    for (int x = startX; x != termX; x += incX)
                    {
                        int localX = (s.NewLine == NewLine.SC || y % 2 == 0) ? x : s.Width - 1 - x;
                        list.Add(new Point(s.X + localX, s.Y + y));
                    }
            }
            else
            {
                for (int x = startX; x != termX; x += incX)
                    for (int y = startY; y != termY; y += incY)
                    {
                        int localY = (s.NewLine == NewLine.SC || x % 2 == 0) ? y : s.Height - 1 - y;
                        list.Add(new Point(s.X + x, s.Y + localY));
                    }
            }
        }

        private static int EmitRegion(byte[] buf, int offset, Section s)
        {
            int startX = (s.StartCorner == StartCorner.TR || s.StartCorner == StartCorner.BR) ? s.Width - 1 : 0;
            int termX  = (s.StartCorner == StartCorner.TR || s.StartCorner == StartCorner.BR) ? -1 : s.Width;
            int incX   = (s.StartCorner == StartCorner.TR || s.StartCorner == StartCorner.BR) ? -1 : 1;
            int startY = (s.StartCorner == StartCorner.BL || s.StartCorner == StartCorner.BR) ? s.Height - 1 : 0;
            int termY  = (s.StartCorner == StartCorner.BL || s.StartCorner == StartCorner.BR) ? -1 : s.Height;
            int incY   = (s.StartCorner == StartCorner.BL || s.StartCorner == StartCorner.BR) ? -1 : 1;

            byte[] lut = _lut;
            if (s.Orientation == Orientation.HZ)
            {
                for (int y = startY; y != termY; y += incY)
                {
                    for (int x = startX; x != termX; x += incX)
                    {
                        int localX = (s.NewLine == NewLine.SC || y % 2 == 0) ? x : s.Width - 1 - x;
                        int globalIndex = (s.Y + y) * Width + (s.X + localX);
                        buf[offset++] = lut[Frame[globalIndex].R];
                        buf[offset++] = lut[Frame[globalIndex].G];
                        buf[offset++] = lut[Frame[globalIndex].B];
                    }
                }
            }
            else // VT
            {
                for (int x = startX; x != termX; x += incX)
                {
                    for (int y = startY; y != termY; y += incY)
                    {
                        int localY = (s.NewLine == NewLine.SC || x % 2 == 0) ? y : s.Height - 1 - y;
                        int globalIndex = (s.Y + localY) * Width + (s.X + x);
                        buf[offset++] = lut[Frame[globalIndex].R];
                        buf[offset++] = lut[Frame[globalIndex].G];
                        buf[offset++] = lut[Frame[globalIndex].B];
                    }
                }
            }
            return offset;
        }

        public static void SetPixel(int x, int y, Pixel color)
        {
            x = x > Width - 1 ? Width - 1 : x < 0 ? 0 : x;
            y = y > Height - 1 ? Height - 1 : y < 0 ? 0 : y;

            Frame[y * Width + x] = color;
            OnFrameChanged();
        }
        public static Pixel GetPixel(int x, int y)
        {
            x = x > Width - 1 ? Width - 1 : x < 0 ? 0 : x;
            y = y > Height - 1 ? Height - 1 : y < 0 ? 0 : y;

            return Frame[y * Width + x];
        }

        public static int GetPixelIntensity(Pixel p)
        {
            return p.R + p.G + p.B;
        }

        public static Int32[] FrameToInt32()
        {
            Int32[] data = new Int32[Width * Height];
            if (SerialManager.ColorMode == SerialManager.CMode.BPP24RGB)
            {
                for (int i = 0; i < Width * Height; i++)
                    data[i] = Frame[i].GetBPP24RGB_Int32();
            }
            else if (SerialManager.ColorMode == SerialManager.CMode.BPP16RGB)
            {
                for (int i = 0; i < Width * Height; i++)
                    data[i] = Frame[i].GetBPP16RGB_Int32();
            }
            else if (SerialManager.ColorMode == SerialManager.CMode.BPP8RGB)
            {
                for (int i = 0; i < Width * Height; i++)
                    data[i] = Frame[i].GetBPP8RGB_Int32();
            }
            else if (SerialManager.ColorMode == SerialManager.CMode.BPP8Gray)
            {
                for (int i = 0; i < Width * Height; i++)
                    data[i] = Frame[i].GetBPP8Grayscale_Int32();
            }
            else if (SerialManager.ColorMode == SerialManager.CMode.BPP1Mono)
            {

                for (int i = 0; i < Width * Height; i++)
                    data[i] = Frame[i].GetBPP1Monochrome_Int32();
            }
            return data;
        }

        public static void InjestGDIBitmap(Bitmap b, InterpolationMode mode)
        {
            BitmapToFrame(b, mode);
            b.Dispose();
        }

        public static void FillFrame(Pixel color)
        {
            for (int i = 0; i < Width * Height; ++i)
            {
                Frame[i] = color;
            }
            //OnFrameChanged();
        }
        private static Pixel GetGradientColor(float percent, Pixel bottomColor, Pixel topColor)
        {
            percent = percent < 0 ? 0 : percent > 1 ? 1 : percent;
            Pixel c = new Pixel(0, 0, 0);
            c.R = (byte)((bottomColor.R * percent) + topColor.R * (1 - percent));
            c.G = (byte)((bottomColor.G * percent) + topColor.G * (1 - percent));
            c.B = (byte)((bottomColor.B * percent) + topColor.B * (1 - percent));
            return c;
        }

        public static void DrawColumnMirrored(int x, int height, Pixel bottomColor, Pixel topColor)
        {
            int gradientIndex = 0;
            Frame[((Height / 2) - 1) * Width + x] = GetGradientColor(1, bottomColor, topColor);
            for (int y = (Height / 2) - 2; y > (Height / 2) - 2 - height; y--)
            {
                if (y < 0)
                    break;
                Pixel color = GetGradientColor((float)y / ((Height / 2) - 1), bottomColor, topColor);
                Frame[y * Width + x] = color;
                Frame[y * Width + x] = color;
                gradientIndex++;
            }
            /* 
            
          //  gradientIndex = 0;
            Frame[(Height / 2) * Width + x] = GetGradientColor(1, bottomColor, topColor);
            for (int y = (Height / 2) + 1; y < (Height / 2) + 1 + height; y++)
            {
                if (y > Height - 1)
                    break;
                Frame[y * Width + x] = GetGradientColor((float)((Height / 2) + 1) / y, bottomColor, topColor);
              //  gradientIndex++;
            }
            */
        }
        public static void DrawColumn(int x, int height, Pixel bottomColor, Pixel topColor)
        {
            int gradientIndex = 0;
            Frame[((Height) - 1) * Width + x] = GetGradientColor(1, bottomColor, topColor);
            for (int y = (Height) - 2; y > (Height) - 2 - height; y--)
            {
                if (y < 0)
                    break;
                Frame[y * Width + x] = GetGradientColor((float)y / ((Height) - 1), bottomColor, topColor);
                gradientIndex++;
            }
        }

        //****************************************************************************************************************************************************
        //************************************ OLD BITMAP PROCESSER CLASS MEMBERS ****************************************************************************
        //****************************************************************************************************************************************************
        public static unsafe void BitmapToFrame(Bitmap bitmap, InterpolationMode mode)
        {
            if (bitmap.Width != Width || bitmap.Height != Height)
                bitmap = DownsampleBitmap(bitmap, Width, Height, mode);

            BitmapData imageData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            int numBytes = imageData.Stride;

            byte* scan0 = (byte*)imageData.Scan0;
            int index = 0;
            for (int i = 0; i < Height; i++)
            {
                for (int e = 0; e < Width; e++)
                {
                    Frame[index].B = *(scan0 + (e * 3));
                    Frame[index].G = *(scan0 + (e * 3) + 1);
                    Frame[index].R = *(scan0 + (e * 3) + 2);
                    index++;
                }
                scan0 += numBytes;
            }
            bitmap.UnlockBits(imageData);
            OnFrameChanged();
        }
        public static Bitmap DownsampleBitmap(Bitmap b, int width, int height, InterpolationMode mode)
        {
            Bitmap result = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.InterpolationMode = mode;
                g.DrawImage(b, 0, 0, width, height);
                g.Save();
            }
            return result;
        }

        // Loads a PNG that is included as a WPF <Resource> in this assembly.
        // Replaces the old Properties.Resources.X path so we don't depend on
        // Resources.resx (which trips MSB3822 on modern .NET SDKs).
        public static Bitmap LoadEmbeddedBitmap(string packPath)
        {
            var sri = System.Windows.Application.GetResourceStream(new Uri(packPath, UriKind.Absolute));
            return new Bitmap(sri.Stream);
        }

        public static BitmapSource CreateBitmapSourceFromBitmap(Bitmap bitmap)
        {
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            var bitmapSource = BitmapSource.Create(bitmapData.Width,
                bitmapData.Height,
                bitmap.HorizontalResolution,
                bitmap.VerticalResolution,
                PixelFormats.Bgra32,
                null,
                bitmapData.Scan0,
                bitmapData.Stride * bitmapData.Height,
                bitmapData.Stride);

            bitmap.UnlockBits(bitmapData);
            return bitmapSource;
        }
        //****************************************************************************************************************************************************
        //************************************ OLD BITMAP PROCESSER CLASS MEMBERS ****************************************************************************
        //****************************************************************************************************************************************************
    }

}
