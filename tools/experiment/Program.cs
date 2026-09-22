using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VesperLab
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    string root = AppDomain.CurrentDomain.BaseDirectory, last = Path.Combine(root, "last-profile.txt");
                    string path = File.Exists(last) ? File.ReadAllText(last).Trim() : "";
                    if (!File.Exists(path)) path = Path.Combine(root, "..", "..", "tools", "experiment", "profiles", "vesper-execute-02.json");
                    ProfileStore.Load(path);
                }
                else
                {
                    if (args.Length < 2 || args[0] != "--profile") throw new ArgumentException("ControlExperiment.exe [--profile <profile.json> [--notice-test | --profile-check | --notice-replay | --ui-smoke ...]]");
                    ProfileStore.Load(args[1]); args = args.Skip(2).ToArray();
                }
                if (args.Length >= 1 && (args[0] == "--notice-test" || args[0] == "--profile-check"))
                {
                    if (args.Length != 2) throw new ArgumentException("--notice-test 또는 --profile-check <report.json>");
                    return NoticeTests.Run(args[1], args[0] == "--profile-check");
                }
                if (args.Length >= 1 && args[0] == "--notice-replay")
                {
                    if (args.Length != 6) throw new ArgumentException("--notice-replay <RGB24-profile-ROI-file> <report.json> <first-low> <first-high> <last-high> (60fps, 0-based)");
                    return NoticeTests.Replay(args[1], args[2], Int32.Parse(args[3]), Int32.Parse(args[4]), Int32.Parse(args[5]));
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length >= 1 && args[0] == "--correction-ui-smoke")
                {
                    if (args.Length != 2) throw new ArgumentException("--correction-ui-smoke <image.png>");
                    TrialCorrectionForm.Smoke(args[1]); return 0;
                }
                if (args.Length >= 1 && args[0] == "--notice-capture-test")
                {
                    if (args.Length != 2) throw new ArgumentException("--notice-capture-test <report.json>");
                    return NoticeTests.CaptureTest(args[1]);
                }
                if (args.Length >= 1 && args[0] == "--ui-smoke")
                {
                    if (args.Length != 2) throw new ArgumentException("--ui-smoke <image.png>");
                    using (var form = new NoticeForm(AppDomain.CurrentDomain.BaseDirectory, true))
                    {
                        form.Show(); Application.DoEvents();
                        form.VerifySmokeSettings();
                        form.Refresh(); Application.DoEvents();
                        using (var bmp = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size)); bmp.Save(args[1]); }
                        form.Close();
                    }
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException("알 수 없는 인수입니다. --profile <profile.json>만 지정하면 문구 감지 실험 창이 열립니다.");
                bool first;
                using (var mutex = new Mutex(true, @"Local\VesperInputExperiment-v1", out first))
                {
                    if (!first) { MessageBox.Show("실험 프로그램이 이미 열려 있습니다."); return 1; }
                    try { Application.Run(new NoticeForm(AppDomain.CurrentDomain.BaseDirectory, false)); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception e)
            {
                if (args.Length == 0 && ProfileStore.Current != null) MessageBox.Show(e.Message, "실험 프로그램 오류");
                else Console.Error.WriteLine(e.ToString());
                return 1;
            }
        }
    }
}
