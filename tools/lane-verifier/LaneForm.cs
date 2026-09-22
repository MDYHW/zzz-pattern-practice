using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VesperLab
{
    public sealed class LaneForm : Form
    {
        private readonly ComboBox profiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
        private readonly ComboBox entry = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly Label conditions = new Label { AutoSize = true, MaximumSize = new Size(900,0) };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(900,0), ForeColor = Color.DarkGreen };
        private readonly Label outcomeLabel = new Label { AutoSize = true, Margin = new Padding(3,9,8,3) };
        private readonly TextBox outcome = new TextBox { Width = 150, Enabled = false };
        private readonly Button save = new Button { Text = "결과 보관", AutoSize = true, Enabled = false };
        private readonly Button cancel = new Button { Text = "중단 (F9)", AutoSize = true, Enabled = false };
        private readonly DataGridView table = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        private readonly string root, logs;
        private readonly string[] ids = { "vesper-execute-01", "vesper-execute-02", "girtablullu-stagnant-execute-01" };
        private readonly ConcurrentQueue<ObservedKey> observed = new ConcurrentQueue<ObservedKey>();
        private readonly LaneOverlay lane = new LaneOverlay();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 16 };
        private ObservedInput hook;
        private LaneRun run;
        private IntPtr game;
        private Rectangle gameBounds;
        private Thread detector;
        private volatile bool cancelDetection;
        private bool closing, selecting, saved;
        private bool Busy { get { return run != null && (run.Status == "armed" || run.Status == "running" || run.Status == "stopping"); } }

        public LaneForm(string directory, bool installHook)
        {
            root = directory; logs = Path.Combine(root, "results");
            Text = "입력 레인 검증 · 수동 플레이 전용"; Font = new Font("맑은 고딕", 10);
            AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(980,640); MinimumSize = new Size(850,550);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "실제 게임에서 레인만 보고 직접 입력합니다. 자동 입력 기능은 없습니다.", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0,0,0,12) },0,0);
            var selection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            selection.Controls.Add(new Label { Text = "검증 대상", AutoSize = true, Margin = new Padding(0,8,8,3) }); selection.Controls.Add(profiles);
            selection.Controls.Add(new Label { Text = "진입 입력", AutoSize = true, Margin = new Padding(18,8,8,3) }); selection.Controls.Add(entry); selection.Controls.Add(cancel);
            layout.Controls.Add(selection,0,1); layout.Controls.Add(conditions,0,2);
            layout.Controls.Add(new Label { Text = "① 게임을 창모드 16:9 · 1280×720 이상으로 배치  ② 게임에서 문구 출현 전에 F8\n③ 레인과 입력선이 겹칠 때 직접 입력  ④ 종료 후 이 창에 게임 결과 0/1 입력\nF9 또는 ESC·게임 창 이탈·창 이동/크기 변경 시 중단합니다. 준비 전투 중 수동 조작은 가능합니다.", AutoSize = true, MaximumSize = new Size(900,0), Margin = new Padding(0,12,0,12) },0,3);
            foreach (string name in new[] { "동작", "키", "이른 값 ms", "늦은 값 ms", "레인 판정 · 게임 성공과 별개" }) table.Columns.Add(name,name);
            table.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize; table.RowTemplate.Height = 30;
            table.Columns[4].FillWeight = 180; layout.Controls.Add(table,0,4);
            var result = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0,10,0,5) };
            result.Controls.Add(outcomeLabel); result.Controls.Add(outcome); result.Controls.Add(save);
            var folder = new Button { Text = "결과 폴더 열기", AutoSize = true }; folder.Click += delegate { Directory.CreateDirectory(logs); Process.Start(logs); }; result.Controls.Add(folder);
            layout.Controls.Add(result,0,5); layout.Controls.Add(status,0,6); Controls.Add(layout);
            profiles.Items.AddRange(new object[] { "베스퍼 · Execute_01", "베스퍼 · Execute_02", "기르타블리르 · Execute_01" });
            profiles.SelectedIndexChanged += delegate { LoadProfile(); };
            entry.SelectedIndexChanged += delegate { if (!selecting) LoadProfile(); };
            outcome.TextChanged += delegate { save.Enabled = !Busy && !saved && run != null && run.Status == "completed" && LaneRun.ValidOutcome(outcome.Text.Trim(), run.Profile.Actions.Length); };
            cancel.Click += delegate { Stop("사용자 F9 / 중단"); };
            save.Click += delegate {
                try { string file = run.Save(logs,outcome.Text.Trim()); saved = true; save.Enabled = false; status.Text = "보관 완료: " + file; }
                catch (Exception e) { status.Text = "저장 실패: " + e.Message; }
            };
            lane.ReadTimeMs = delegate { return run != null && run.NoticeOriginMs.HasValue ? run.TimeAt(Stopwatch.GetTimestamp()) : 0; };
            lane.Painted += p => { if (run != null && run.Status == "running") run.Paints.Add(p); };
            profiles.SelectedIndex = 0;
            if (installHook) { hook = new ObservedInput(); hook.Received += key => observed.Enqueue(key); }
            timer.Tick += delegate { Tick(); }; timer.Start();
        }
        private void LoadProfile()
        {
            if (selecting || Busy || profiles.SelectedIndex < 0) return;
            selecting = true;
            try
            {
                string chosen = entry.SelectedItem as string;
                ProfileStore.Load(Path.Combine(root,"profiles",ids[profiles.SelectedIndex] + ".json"));
                bool spaceAvailable = ProfileStore.Current.BossId == "vesper";
                entry.Items.Clear(); entry.Items.Add("RMB"); if (spaceAvailable) entry.Items.Add("Space");
                entry.SelectedItem = chosen == "Space" && spaceAvailable ? "Space" : "RMB";
                if ((string)entry.SelectedItem == "Space")
                {
                    var copy = ProfileStore.Snapshot(); copy.Actions[0].Key = "Space";
                    if (copy.Id == "vesper-execute-02") copy.Actions[0].Timing.EarlyMs = 2900;
                    ProfileStore.Apply(copy);
                }
                run = null; saved = false; outcome.Clear(); outcome.Enabled = false; save.Enabled = false;
                conditions.Text = ProfileStore.Current.Conditions;
                outcomeLabel.Text = "게임 결과 " + ProfileStore.Current.Actions.Length + "자리 (진입부터 1성공 / 0실패)";
                outcome.MaxLength = ProfileStore.Current.Actions.Length;
                table.Rows.Clear(); foreach (var a in ProfileStore.Current.Actions) table.Rows.Add(a.Label,a.Key,a.Timing.EarlyMs,a.Timing.LateMs,"—");
                status.Text = "준비 완료 · 기존 자동 입력 실험기는 종료하고, 게임 창에서 문구 출현 전에 F8을 누르세요. 결과 미입력 회차는 다음 F8·종료 시 폐기합니다.";
            }
            finally { selecting = false; }
        }
        private void SetBusy(bool value)
        { profiles.Enabled = entry.Enabled = !value; cancel.Enabled = value; outcome.Enabled = !value && run != null && run.Status == "completed"; save.Enabled = false; }
        private void Arm(ObservedKey key)
        {
            if (Busy) return;
            if (Process.GetProcessesByName("ControlExperiment").Length > 0) { status.Text = "기존 ControlExperiment를 먼저 종료하세요. F8 자동 입력과 동시에 실행할 수 없습니다."; return; }
            uint pid; Native.GetWindowThreadProcessId(key.ForegroundWindow,out pid);
            using (var process = Process.GetProcessById((int)pid))
                if (!String.Equals(process.ProcessName,"ZenlessZoneZero",StringComparison.OrdinalIgnoreCase)) { status.Text = "게임 창에서 F8을 누르세요. 이 창에서는 시작하지 않습니다."; return; }
            BeginTrial(key);
        }
        private void BeginTrial(ObservedKey key)
        {
            game = key.ForegroundWindow;
            if (Native.GetForegroundWindow() != game) return;
            gameBounds = Native.ClientBounds(game);
            var probe = new GameNoticeProbe(game);
            run = new LaneRun { ArmedQpc = key.Qpc, Profile = ProfileStore.Snapshot(), SourceProfileHash = ProfileStore.SourceHash, ProfileHash = ProfileStore.Hash, TemplateHash = ProfileStore.TemplateHash,
                ClientX = gameBounds.X, ClientY = gameBounds.Y, ClientWidth = gameBounds.Width, ClientHeight = gameBounds.Height,
                DetectorX = probe.Region.X, DetectorY = probe.Region.Y, DetectorWidth = probe.Region.Width, DetectorHeight = probe.Region.Height,
                LaneAccepted = new bool[ProfileStore.Current.Actions.Length] };
            saved = false; outcome.Clear(); SetBusy(true); lane.Hide();
            foreach (DataGridViewRow row in table.Rows) row.Cells[4].Value = "감지 대기";
            status.Text = "감지 대기 · 문구가 없는 상태 100ms 이후 새 출현을 기다립니다. 게임을 직접 조작하세요.";
            cancelDetection = false; var current = run;
            detector = new Thread(delegate() { Detect(current,probe); }) { IsBackground = true, Name = "Lane notice capture" }; detector.Start();
        }
        private void Post(Action action)
        { if (closing || IsDisposed || !IsHandleCreated) return; try { BeginInvoke(action); } catch (InvalidOperationException) { } }
        private void Detect(LaneRun current, GameNoticeProbe probe)
        {
            try
            {
                var clock = new StopwatchClock(); var edge = new NoticeEdge();
                Func<bool> stopped = () => cancelDetection || Native.GetForegroundWindow() != game;
                while (clock.ElapsedMs(current.ArmedQpc) < current.Profile.Detector.TimeoutMs)
                {
                    if (stopped()) throw new OperationCanceledException("감지 중 게임 창 이탈 또는 중단");
                    var sample = probe.Capture(clock,current.ArmedQpc); current.Samples.Add(sample);
                    if (stopped()) throw new OperationCanceledException("감지 중 게임 창 이탈 또는 중단");
                    if (sample.AnalysisEndMs - sample.CaptureBeginMs > 100) throw new InvalidOperationException("감지 캡처·분석이 100ms를 초과하여 중단했습니다.");
                    if (edge.Push(sample,current.Samples.Count-1,current.Samples))
                    {
                        current.FirstMatchIndex = edge.First; current.ConfirmedIndex = edge.Confirmed;
                        current.NoticeOriginMs = current.Samples[edge.First].CaptureEndMs;
                        Post(delegate {
                            detector = null;
                            if (cancelDetection || Native.GetForegroundWindow() != game || Native.ClientBounds(game) != gameBounds) { End("interrupted","감지 확정 후 창 변경 또는 중단"); return; }
                            lane.Configure(gameBounds);
                            if (lane.Bounds.IntersectsWith(probe.Region)) { End("interrupted","레인이 감지 영역과 겹쳐 중단했습니다."); return; }
                            if (!SystemInformation.VirtualScreen.Contains(lane.Bounds)) { End("interrupted","레인이 화면 밖입니다. 게임 창 전체를 화면 안에 배치한 후 다시 F8을 누르세요."); return; }
                            current.Status = "running"; lane.SetFrame(current.TimeAt(Stopwatch.GetTimestamp()),current.Profile.Actions,current.LaneAccepted,"직접 입력 · F9 중단"); lane.Show();
                            status.Text = "문구 감지 · 레인 진행 중. 진입부터 직접 입력하세요.";
                        });
                        return;
                    }
                    Thread.Sleep(1); clock.WaitUntil(current.ArmedQpc,sample.CaptureBeginMs + current.Profile.Detector.PollMs,stopped);
                }
                throw new TimeoutException("새 문구를 감지하지 못했습니다. 문구 출현 전에 F8을 눌러주세요.");
            }
            catch (Exception e) { Post(delegate { detector = null; End("interrupted",e.Message); }); }
            finally { probe.Dispose(); }
        }
        private void Stop(string reason)
        {
            if (!Busy) return;
            cancelDetection = true; lane.Hide();
            if (detector != null) { run.Status = "stopping"; status.Text = "중단 중…"; }
            else End("interrupted",reason);
        }
        private void End(string state,string message)
        {
            if (run == null) return;
            run.Status = state; run.EndedUtc = DateTime.UtcNow.ToString("o"); if (state != "completed") run.Error = message;
            lane.Hide(); SetBusy(false); status.Text = message;
            for (int i = 0; i < run.Profile.Actions.Length; i++)
            {
                var accepted = run.Inputs.FirstOrDefault(p => p.ActionId == run.Profile.Actions[i].Id && p.InsideWindow);
                table.Rows[i].Cells[4].Value = accepted == null ? "구간 내 입력 없음" : "구간 내 " + accepted.TimeMs.ToString("F1") + "ms";
            }
        }
        private void Tick()
        {
            try
            {
                if (Busy && Native.GetForegroundWindow() != game) Stop("게임 창 포커스 이탈");
                ObservedKey key;
                while (observed.TryDequeue(out key))
                {
                    if (!key.Down || key.Injected) continue;
                    if (key.Key == "F8") { Arm(key); continue; }
                    if (key.Key == "F9" || key.Key == "Escape") { Stop("사용자 " + key.Key + " 중단"); continue; }
                    if (run != null && run.Status == "running" && key.ForegroundWindow == game && (key.Key == "RMB" || key.Key == "Space"))
                        run.AddInput(key.Key,key.Qpc,unchecked(key.ReceivedOsTimeMs-key.OsTimeMs),Stopwatch.GetTimestamp());
                }
                if (run == null || run.Status != "running") return;
                if (Native.ClientBounds(game) != gameBounds) { Stop("게임 창 이동 또는 크기 변경"); return; }
                double time = run.TimeAt(Stopwatch.GetTimestamp());
                if (time > run.Profile.Actions.Last().Timing.LateMs + 500) { End("completed","레인 완료 · 게임의 진입부터 마지막 타까지 0/1을 입력해야 회차를 보관합니다."); return; }
                lane.SetFrame(time,run.Profile.Actions,run.LaneAccepted,"레인 내 " + run.LaneAccepted.Count(x=>x) + "/" + run.Profile.Actions.Length + " · 게임 결과 별도 입력");
            }
            catch (Exception e) { if (Busy) Stop(e.Message); status.Text = e.Message; }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        { closing = true; cancelDetection = true; timer.Stop(); if (detector != null) detector.Join(300); if (hook != null) hook.Dispose(); lane.Dispose(); base.OnFormClosing(e); }
    }
}
