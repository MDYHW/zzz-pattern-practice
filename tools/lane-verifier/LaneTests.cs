using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VesperLab
{
    internal static class LaneTests
    {
        private static void Check(bool ok,string name) { if (!ok) throw new Exception("FAILED: " + name); }
        private static readonly string[] ids = { "vesper-execute-01", "vesper-execute-02", "girtablullu-stagnant-execute-01" };
        public static int Run(string root,string directory)
        {
            Directory.CreateDirectory(directory); var checks = new List<string>();
            foreach (string id in ids)
            {
                ProfileStore.Load(Path.Combine(root,"profiles",id + ".json"));
                var matcher = new NoticeMatcher();
                using (var image = new Bitmap(ProfileStore.OpenTemplate())) Check(matcher.Score(image) > .99,"template " + id);
                using (var blank = new Bitmap(NoticeMatcher.Width,NoticeMatcher.Height)) Check(matcher.Score(blank) == 0,"blank " + id);
                var edge = new NoticeEdge(); var samples = new List<NoticeSample>();
                foreach (int ms in new[] { 0,50,100,120,140,160 })
                {
                    var sample = new NoticeSample { CaptureBeginMs = ms, CaptureEndMs = ms+1, AnalysisEndMs = ms+2, Present = ms>=120 };
                    samples.Add(sample); bool found=edge.Push(sample,samples.Count-1,samples);
                    Check(found==(ms==160),"confirmation edge " + ms);
                }
                Check(samples[edge.First].CaptureEndMs==121&&samples[edge.Confirmed].CaptureEndMs==161,"first-match origin, not confirm");
                var run = new LaneRun { ArmedQpc = 1000, Frequency = 1000, NoticeOriginMs = 121, Profile = ProfileStore.Snapshot(), LaneAccepted = new bool[ProfileStore.Current.Actions.Length] };
                for (int i=0;i<run.Profile.Actions.Length;i++)
                {
                    var a=run.Profile.Actions[i]; long qpc=run.ArmedQpc+121+(long)a.Timing.BaselineMs;
                    run.AddInput(a.Key,qpc,0,qpc+16);
                }
                Check(run.LaneAccepted.All(x=>x),"all adopted reference inputs");
                Check(run.Inputs.All(x=>Math.Abs(x.HookToUiMs-16)<.001),"queue delay excluded from input time");
                int accepted=run.Inputs.Count(x=>x.Accepted); var first=run.Profile.Actions[0];
                run.AddInput(first.Key,run.ArmedQpc+121+(long)first.Timing.BaselineMs,0,run.ArmedQpc+121+(long)first.Timing.BaselineMs);
                Check(run.Inputs.Count(x=>x.Accepted)==accepted,"second press does not count twice");
                Check(run.Inputs.Last().InsideWindow&&!run.Inputs.Last().Accepted,"duplicate retains literal in-band evidence");
                var late=new LaneRun {ArmedQpc=0,Frequency=1000,NoticeOriginMs=0,Profile=run.Profile,LaneAccepted=new bool[run.Profile.Actions.Length]};
                late.AddInput(first.Key,(long)first.Timing.LateMs+1,0,(long)first.Timing.LateMs+1);
                Check(!late.LaneAccepted[0],"late input rejected");
                var early=new LaneRun {ArmedQpc=0,Frequency=1000,NoticeOriginMs=0,Profile=run.Profile,LaneAccepted=new bool[run.Profile.Actions.Length]};
                early.AddInput(first.Key,(long)first.Timing.EarlyMs-1,0,(long)first.Timing.EarlyMs-1);
                early.AddInput(first.Key,(long)first.Timing.EarlyMs,0,(long)first.Timing.EarlyMs);
                Check(early.LaneAccepted[0],"early extra allows later recovery");
                Check(!LaneRun.ValidOutcome("111",run.Profile.Actions.Length)&&!LaneRun.ValidOutcome(new string('2',run.Profile.Actions.Length),run.Profile.Actions.Length),"invalid game outcomes");
                bool refused=false;try {run.Save(directory,new string('1',run.Profile.Actions.Length));}catch(InvalidOperationException){refused=true;}Check(refused,"unfinished run not saved");
                run.Status="completed";string saved=run.Save(directory,new string('1',run.Profile.Actions.Length));Check(File.Exists(saved),"completed outcome saved");
                checks.Add(id + ": template, origin, judgment, persistence passed");
            }
            foreach (var type in typeof(LaneTests).Assembly.GetTypes()) foreach (var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance))
            {
                var import=(DllImportAttribute)Attribute.GetCustomAttribute(method,typeof(DllImportAttribute));
                if(import!=null)Check(!new[]{"SendInput","keybd_event","mouse_event","SetForegroundWindow"}.Contains(import.EntryPoint),"no injection/focus API");
            }
            // Hook startup/disposal and repeat-state logic: invokes our pure publication
            // method directly, never synthesizes or submits an OS/game input.
            using (var observer=new ObservedInput())
            {
                int presses=0;observer.Received+=k=>{if(k.Down)presses++;};
                var publish=typeof(ObservedInput).GetMethod("Publish",BindingFlags.NonPublic|BindingFlags.Instance);
                publish.Invoke(observer,new object[]{"Space",true,1L,1u,1u});publish.Invoke(observer,new object[]{"Space",true,2L,2u,2u});
                publish.Invoke(observer,new object[]{"Space",false,3L,3u,3u});publish.Invoke(observer,new object[]{"Space",true,4L,4u,4u});
                Check(presses==2,"held-key repeat suppressed");
            }
            checks.Add("no input injection APIs; passive hooks install/dispose; repeated down suppressed");
            File.WriteAllText(Path.Combine(directory,"self-test.json"),new JavaScriptSerializer().Serialize(new {passed=true,checks=checks,gameInputsSent=0}));return 0;
        }
        public static int Smoke(string root,string directory)
        {
            Directory.CreateDirectory(directory);var checks=new List<string>();
            foreach (string id in ids)
            {
                ProfileStore.Load(Path.Combine(root,"profiles",id+".json"));
                using (var fake=new Form {Text="Synthetic notice capture — no game input",AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(1280,720),StartPosition=FormStartPosition.Manual,Location=new Point(50,50),BackColor=Color.Black})
                using (var image=new Bitmap(ProfileStore.OpenTemplate()))
                {
                    fake.TopMost=true;fake.Show();fake.Hide();fake.Show();fake.Activate();fake.Refresh();Application.DoEvents();Thread.Sleep(200);
                    var bounds=Native.ClientBounds(fake.Handle);
                    using(var probe=new GameNoticeProbe(fake.Handle))
                    {
                        var clock=new StopwatchClock();long origin=clock.Timestamp;Check(!probe.Capture(clock,origin).Present,"blank fake window");
                        var d=ProfileStore.Current.Detector;fake.Controls.Add(new PictureBox{Image=image,Location=new Point(d.X,d.Y),Size=new Size(d.Width,d.Height)});
                        fake.Refresh();Application.DoEvents();Thread.Sleep(200);
                        var sample=probe.Capture(clock,origin);
                        if(!sample.Present){probe.SaveDetectedImage(Path.Combine(directory,id+"-failed-roi.png"));using(var bmp=new Bitmap(fake.Width,fake.Height)){fake.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));bmp.Save(Path.Combine(directory,id+"-fake.png"));}File.WriteAllText(Path.Combine(directory,"failed-geometry.txt"),"client="+bounds+" ROI="+probe.Region+" score="+sample.Score);}
                        Check(sample.Present,"windowed template " + id);
                        using(var lane=new LaneOverlay())
                        {
                            lane.Configure(bounds);Check(!lane.Bounds.IntersectsWith(probe.Region),"lane avoids notice ROI");
                            IntPtr before=Native.GetForegroundWindow();lane.SetFrame(ProfileStore.Current.Actions[0].Timing.BaselineMs,ProfileStore.Current.Actions,new bool[ProfileStore.Current.Actions.Length],"무입력 화면 검증");lane.Show();lane.Refresh();Application.DoEvents();
                            Check(Native.GetForegroundWindow()==before,"overlay must not take focus");
                            long style=GetWindowLongPtr(lane.Handle,-20).ToInt64();Check((style&0x08000020)==0x08000020,"nonactivate and input-transparent styles");
                            Check(WindowFromPoint(new Point(lane.Left+20,lane.Top+60))==fake.Handle,"native point hit test reaches game surface");
                            Check(SendMessage(lane.Handle,0x84,IntPtr.Zero,IntPtr.Zero)==new IntPtr(-1),"hit test passes through");
                            using(var bmp=new Bitmap(lane.Width,lane.Height)){lane.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));bmp.Save(Path.Combine(directory,id+"-lane.png"));}
                            double observed=-1;lane.ReadTimeMs=()=>9000;lane.Painted+=p=>observed=p.TimeMs;lane.SetFrame(0,ProfileStore.Current.Actions,new bool[ProfileStore.Current.Actions.Length],"지연 프레임 절대시각 검사");lane.Refresh();Application.DoEvents();Check(observed==9000,"paint reads current clock not stale timer");
                            lane.Hide();lane.Configure(new Rectangle(bounds.X,SystemInformation.VirtualScreen.Bottom,1280,720));Check(!SystemInformation.VirtualScreen.Contains(lane.Bounds),"off-screen lane guard condition");
                        }
                        fake.Left+=20;bool rejected=false;try{probe.CheckGeometry();}catch(InvalidOperationException){rejected=true;}Check(rejected,"moved window rejected");
                    }
                    fake.ClientSize=new Size(1600,900);fake.Location=new Point(20,20);
                    var scaled=Native.ClientBounds(fake.Handle);
                    Check(SystemInformation.VirtualScreen.Contains(scaled),"test desktop accommodates scaled window");
                    var pic=(PictureBox)fake.Controls[0];var detector=ProfileStore.Current.Detector;
                    pic.Location=new Point((int)Math.Round(scaled.Width*(double)detector.X/detector.ReferenceWidth),(int)Math.Round(scaled.Height*(double)detector.Y/detector.ReferenceHeight));
                    pic.Size=new Size((int)Math.Round(scaled.Width*(double)detector.Width/detector.ReferenceWidth),(int)Math.Round(scaled.Height*(double)detector.Height/detector.ReferenceHeight));pic.SizeMode=PictureBoxSizeMode.StretchImage;
                    fake.Refresh();Application.DoEvents();Thread.Sleep(200);
                    using(var probe=new GameNoticeProbe(fake.Handle)){var clock=new StopwatchClock();Check(probe.Capture(clock,clock.Timestamp).Present,"1600x900 scaled window template "+id);}
                    fake.Close();checks.Add(id+": offset 1280x720 and scaled1600x900 capture, overlay geometry/focus/native pass-through, absolute paint clock, movement/off-screen guards");
                }
            }
            using(var form=new LaneForm(root,false))
            using(var fake=new Form {Text="Synthetic controller lifecycle",AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(1280,720),StartPosition=FormStartPosition.Manual,Location=new Point(50,50),BackColor=Color.Black,TopMost=true})
            using(var image=new Bitmap(ProfileStore.OpenTemplate()))
            {
                var trace=new List<string>();
                Action<string> mark=s=>{uint pid;var fg=Native.GetForegroundWindow();Native.GetWindowThreadProcessId(fg,out pid);string process="unknown";try{using(var p=Process.GetProcessById((int)pid))process=p.ProcessName;}catch{}trace.Add(s+" fg="+fg+" process="+process);};
                fake.Activated+=delegate{mark("fake activated");};fake.Deactivate+=delegate{mark("fake deactivated");};form.Activated+=delegate{mark("control activated");};form.Deactivate+=delegate{mark("control deactivated");};
                form.Show();form.Refresh();Application.DoEvents();using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));bmp.Save(Path.Combine(directory,"control.png"));}
                fake.Show();fake.Hide();fake.Show();fake.Activate();fake.Refresh();Application.DoEvents();Thread.Sleep(100);
                // BeginTrial is downstream of the production foreground-process check.
                // Only this synthetic test bypasses that check; no game or OS input is sent.
                mark("before arm");typeof(LaneForm).GetMethod("BeginTrial",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{new ObservedKey {Key="F8",Down=true,Qpc=Stopwatch.GetTimestamp(),ForegroundWindow=fake.Handle}});mark("after arm");
                var end=Stopwatch.StartNew();while(end.ElapsedMilliseconds<200){Application.DoEvents();Thread.Sleep(5);}
                var d=ProfileStore.Current.Detector;fake.Controls.Add(new PictureBox {Image=image,Location=new Point(d.X,d.Y),Size=new Size(d.Width,d.Height)});fake.Refresh();
                var field=typeof(LaneForm).GetField("run",BindingFlags.NonPublic|BindingFlags.Instance);LaneRun run;
                do{Application.DoEvents();Thread.Sleep(5);run=(LaneRun)field.GetValue(form);}while(run.Status=="armed"&&end.ElapsedMilliseconds<3000);
                mark("after detection "+run.Status+" "+run.Error);File.WriteAllLines(Path.Combine(directory,"lifecycle-trace.txt"),trace);
                Check(run.Status=="running","controller detected synthetic notice: "+run.Status+" "+run.Error);
                Check(run.Paints.Count>0,"controller paints live lane");
                Check(run.Paints[0].TimeMs>=ProfileStore.Current.Detector.ConfirmationMs,"first paint preserves elapsed confirmation time");
                fake.Hide();form.Activate();end.Restart();do{Application.DoEvents();Thread.Sleep(5);}while(run.Status=="running"&&end.ElapsedMilliseconds<1000);
                Check(run.Status=="interrupted","controller focus-loss interruption");
                Check(run.GameOutcome==null&&run.Inputs.Count==0,"no fabricated game outcomes/inputs");form.Close();fake.Close();
                checks.Add("controller detection-to-paint bridge, original origin retained, focus loss aborts, no fabricated inputs/results");
            }
            File.WriteAllText(Path.Combine(directory,"smoke.json"),new JavaScriptSerializer().Serialize(new{passed=true,checks=checks,gameInputsSent=0}));return 0;
        }
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    }
}
