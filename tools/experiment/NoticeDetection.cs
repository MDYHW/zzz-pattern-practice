using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VesperLab
{
    public sealed class NoticeSample
    {
        public double CaptureBeginMs, CaptureEndMs, AnalysisEndMs, Score;
        public bool Present;
    }

    // Match the yellow word shapes, not arbitrary yellow area or OCR output.
    public sealed class NoticeMatcher
    {
        public static int Width { get { return ProfileStore.Current.Detector.Width; } }
        public static int Height { get { return ProfileStore.Current.Detector.Height; } }
        public static double Threshold { get { return ProfileStore.Current.Detector.Threshold; } }
        private readonly bool[] template;
        private readonly int count;
        public NoticeMatcher()
        {
            using (var stream = ProfileStore.OpenTemplate())
            using (var image = new Bitmap(stream)) { template = Mask(image); }
            count = template.Count(v => v);
            if (count < 100) throw new InvalidDataException("문구 템플릿이 유효하지 않습니다.");
        }
        private static bool[] Mask(Bitmap image)
        {
            using (var normalized = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(normalized))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(image, new Rectangle(0, 0, Width, Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                }
                BitmapData data = normalized.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var bytes = new byte[data.Stride * Height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    var mask = new bool[Width * Height];
                    for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                    {
                        int p = y * data.Stride + x * 3, b = bytes[p], g = bytes[p + 1], r = bytes[p + 2];
                        mask[y * Width + x] = r > 140 && g > 125 && b < 120 && r - b > 60 && g - b > 50;
                    }
                    return mask;
                }
                finally { normalized.UnlockBits(data); }
            }
        }
        public double Score(Bitmap image)
        {
            bool[] mask = Mask(image); int hits = 0, total = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) { total++; if (template[i]) hits++; }
            return 2.0 * hits / (count + total);
        }
    }

    public interface INoticeProbe : IDisposable
    {
        NoticeSample Capture(ITrialClock clock, long origin);
        void CheckGeometry();
        void SaveDetectedImage(string path);
    }

    public sealed class GameNoticeProbe : INoticeProbe
    {
        private readonly IntPtr window;
        private readonly Rectangle initial;
        private readonly NoticeMatcher matcher = new NoticeMatcher();
        private Bitmap last;
        public readonly Rectangle Region;
        public int ClientWidth { get { return initial.Width; } }
        public int ClientHeight { get { return initial.Height; } }
        public GameNoticeProbe(IntPtr handle)
        {
            window = handle; initial = Client();
            var d = ProfileStore.Current.Detector;
            if (initial.Width < d.MinimumClientWidth || initial.Height <= 0 || Math.Abs(initial.Width / (double)initial.Height - d.ReferenceWidth / (double)d.ReferenceHeight) > .01)
                throw new InvalidOperationException("프로필의 화면 비율·최소 크기 조건이 필요합니다.");
            Region = new Rectangle(initial.X + (int)Math.Round(initial.Width * (double)d.X / d.ReferenceWidth),
                initial.Y + (int)Math.Round(initial.Height * (double)d.Y / d.ReferenceHeight),
                (int)Math.Round(initial.Width * (double)d.Width / d.ReferenceWidth), (int)Math.Round(initial.Height * (double)d.Height / d.ReferenceHeight));
            CheckGeometry();
        }
        private Rectangle Client()
        {
            Native.RECT r; var p = new Native.POINT();
            if (!Native.GetClientRect(window, out r) || !Native.ClientToScreen(window, ref p))
                throw new InvalidOperationException("게임 창 영역을 읽지 못했습니다.");
            return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
        }
        public void CheckGeometry()
        {
            if (Client() != initial || !SystemInformation.VirtualScreen.Contains(Region))
                throw new InvalidOperationException("게임 창이 이동·크기 변경되었거나 감지 영역이 화면 밖입니다.");
        }
        public NoticeSample Capture(ITrialClock clock, long origin)
        {
            var sample = new NoticeSample { CaptureBeginMs = clock.ElapsedMs(origin) };
            CheckGeometry();
            if (last == null) last = new Bitmap(Region.Width, Region.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(last)) g.CopyFromScreen(Region.Location, Point.Empty, Region.Size, CopyPixelOperation.SourceCopy);
            sample.CaptureEndMs = clock.ElapsedMs(origin);
            sample.Score = matcher.Score(last); sample.Present = sample.Score >= NoticeMatcher.Threshold;
            sample.AnalysisEndMs = clock.ElapsedMs(origin); return sample;
        }
        public void SaveDetectedImage(string path) { if (last != null) last.Save(path, ImageFormat.Png); }
        public void Dispose() { if (last != null) last.Dispose(); }
    }

    // Initial visible text must disappear before a new edge can be accepted.
    public sealed class NoticeEdge
    {
        private double absentStart = -1;
        private bool armed;
        public int PreviousAbsent = -1, First = -1, Confirmed = -1;
        public bool Push(NoticeSample sample, int index, IList<NoticeSample> samples)
        {
            if (!sample.Present)
            {
                First = -1; PreviousAbsent = index;
                if (absentStart < 0) absentStart = sample.CaptureEndMs;
                if (sample.CaptureEndMs - absentStart >= ProfileStore.Current.Detector.AbsentMs) armed = true;
                return false;
            }
            absentStart = -1;
            if (!armed) return false;
            if (First < 0) First = index;
            else if (sample.CaptureBeginMs - samples[First].CaptureBeginMs >= ProfileStore.Current.Detector.ConfirmationMs) { Confirmed = index; return true; }
            return false;
        }
    }

}
