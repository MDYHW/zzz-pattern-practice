using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace VesperLab
{
    internal static class LaneProgram
    {
        [STAThread] private static int Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                string root = AppDomain.CurrentDomain.BaseDirectory;
                if (args.Length == 2 && args[0] == "--self-test") return LaneTests.Run(root,args[1]);
                if (args.Length == 2 && args[0] == "--smoke") return LaneTests.Smoke(root,args[1]);
                if (args.Length != 0) throw new ArgumentException("LaneVerifier.exe 또는 --self-test/--smoke <출력 폴더>");
                bool first;
                using (var mutex = new Mutex(true,"Local\\ZZZManualLaneVerifier",out first))
                {
                    if (!first) { MessageBox.Show("입력 레인 검증 프로그램이 이미 실행 중입니다."); return 1; }
                    using (var form = new LaneForm(root,true)) Application.Run(form);
                }
                return 0;
            }
            catch (Exception e)
            {
                if (args.Length > 0) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-error.txt"),e.ToString()); return 1; }
                MessageBox.Show(e.Message,"입력 레인 검증 실행 오류",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1;
            }
        }
    }
}
