using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VesperLab
{
    public sealed class LanePaint
    {
        public double TimeMs;
        public long BeginQpc;
        public long EndQpc;
    }

    public sealed class LaneOverlay : Form
    {
        private ActionProfile[] actions = new ActionProfile[0];
        private bool[] hit = new bool[0];
        private double timeMs;
        private string message = "";
        private readonly Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold);
        private readonly Font detailFont = new Font("Segoe UI", 8);
        private readonly SolidBrush neutralBrush = new SolidBrush(Color.FromArgb(126, 132, 140));
        private readonly SolidBrush currentBrush = new SolidBrush(Color.FromArgb(213, 245, 41));
        private readonly SolidBrush hitBrush = new SolidBrush(Color.FromArgb(45, 200, 141));
        private readonly SolidBrush missBrush = new SolidBrush(Color.FromArgb(232, 91, 94));
        private readonly Pen edgePen = new Pen(Color.FromArgb(235, 240, 244));
        private readonly Pen linePen = new Pen(Color.White, 2);
        private readonly StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        public Func<double> ReadTimeMs { get; set; }
        public event Action<LanePaint> Painted;

        public LaneOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            // Keep Form.TopMost false: Form.SetVisibleCore otherwise explicitly
            // focuses the form after showing, even with ShowWithoutActivation.
            // Native WS_EX_TOPMOST / SetWindowPos below provide the z-order.
            BackColor = Color.FromArgb(23, 27, 31);
            Opacity = 0.94;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000 | 0x00000008;
                return parameters;
            }
        }

        public void Configure(Rectangle gameClient)
        {
            int margin = Math.Min(24, Math.Max(0, gameClient.Width / 40));
            int width = Math.Max(1, gameClient.Width - 2 * margin);
            int height = Math.Min(130, Math.Max(1, gameClient.Height - 2 * margin));
            Rectangle target = new Rectangle(gameClient.Left + margin,
                gameClient.Bottom - height - margin, width, height);
            if (Bounds != target)
                SetWindowPos(Handle, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, 0x0010);
        }

        public void SetFrame(double currentTimeMs, ActionProfile[] currentActions, bool[] currentHit, string currentMessage)
        {
            timeMs = currentTimeMs;
            actions = currentActions ?? new ActionProfile[0];
            hit = currentHit ?? new bool[0];
            message = currentMessage ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            long beginQpc = Stopwatch.GetTimestamp();
            double paintedTimeMs = ReadTimeMs == null ? timeMs : ReadTimeMs();
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.Clear(BackColor);
            float width = ClientSize.Width;
            float inputX = 0.20f * width;
            float bandTop = 58;
            float bandHeight = 26;
            graphics.DrawString(message, detailFont, Brushes.WhiteSmoke, new RectangleF(10, 7, width - 20, 18));
            graphics.DrawString("초록색 = 레인 구간 내 입력 · 게임 성공 여부는 종료 후 별도 기록", detailFont,
                Brushes.LightGray, new RectangleF(10, ClientSize.Height - 22, width - 20, 18));

            for (int index = 0; index < actions.Length; index++)
            {
                ActionProfile action = actions[index];
                if (action == null || action.Timing == null) continue;
                double earlyMs = action.Timing.EarlyMs;
                double lateMs = action.Timing.LateMs;
                float x = inputX + (float)((earlyMs - paintedTimeMs) / 3000.0 * width);
                float tileWidth = (float)((lateMs - earlyMs) / 3000.0 * width);
                if (tileWidth <= 0 || x > width || x + tileWidth < 0) continue;
                bool wasHit = index < hit.Length && hit[index];
                Brush brush = wasHit ? hitBrush : paintedTimeMs > lateMs ? missBrush :
                    paintedTimeMs >= earlyMs ? currentBrush : neutralBrush;
                graphics.FillRectangle(brush, x, bandTop, tileWidth, bandHeight);
                graphics.DrawLine(edgePen, x, bandTop - 2, x, bandTop + bandHeight + 2);
                graphics.DrawLine(edgePen, x + tileWidth, bandTop - 2, x + tileWidth, bandTop + bandHeight + 2);
                float center = x + tileWidth / 2;
                string label = (action.Label ?? action.Id ?? "") + " / " + (action.Key ?? "");
                graphics.DrawString(label, labelFont, Brushes.WhiteSmoke,
                    new RectangleF(center - 100, 28, 200, 24), centered);
                if (wasHit)
                    graphics.DrawString("입력", detailFont, Brushes.Black, new RectangleF(x, bandTop, tileWidth, bandHeight), centered);
            }

            graphics.DrawLine(linePen, inputX, 26, inputX, 94);
            graphics.DrawString("입력선", detailFont, Brushes.White, new RectangleF(inputX - 24, 88, 48, 17), centered);
            base.OnPaint(e);
            long endQpc = Stopwatch.GetTimestamp();
            Action<LanePaint> callback = Painted;
            if (callback != null) callback(new LanePaint { TimeMs = paintedTimeMs, BeginQpc = beginQpc, EndQpc = endQpc });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; }
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                labelFont.Dispose(); detailFont.Dispose(); centered.Dispose();
                neutralBrush.Dispose(); currentBrush.Dispose(); hitBrush.Dispose(); missBrush.Dispose();
                edgePen.Dispose(); linePen.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
