using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VesperLab
{
    public sealed class NoticeForm : Form
    {
        private readonly string root;
        private readonly bool smoke;
        private readonly FlowLayoutPanel settingsPanel = new FlowLayoutPanel();
        private readonly ComboBox profiles = new ComboBox();
        private readonly TextBox profileId = new TextBox(), bossId = new TextBox(), boss = new TextBox(), skill = new TextBox(), conditions = new TextBox(), party = new TextBox(), start = new TextBox();
        private readonly Label count = new Label();
        private readonly Button minus = new Button(), plus = new Button();
        private readonly Label editTarget = new Label(), settingsState = new Label();
        private readonly Label instructionNote = new Label { AutoSize = true, MaximumSize = new Size(940, 0) };
        private readonly Label preparationSummary = new Label { AutoSize = true, MaximumSize = new Size(940, 0) };
        private readonly Label startAttackSummary = new Label { AutoSize = true, MaximumSize = new Size(940, 0) };
        private readonly DataGridView auxiliary = new DataGridView(), recentAuxiliary = new DataGridView();
        private readonly Label recentAuxiliaryTitle = new Label { AutoSize = true, Text = "당시 보조 입력 · 문구 기준 · 결과 자리에는 포함하지 않습니다." };
        private readonly System.Windows.Forms.Timer settingsTimer = new System.Windows.Forms.Timer();
        private readonly CheckBox live = new CheckBox();
        private readonly DataGridView timing = new DataGridView(), recentTiming = new DataGridView();
        private readonly DataGridView preparation = new DataGridView(), recentPreparation = new DataGridView();
        private readonly Label recentPreparationTitle = new Label { AutoSize = true, Text = "당시 준비 대응 · 아래 값은 저장된 회차의 설정입니다." };
        private readonly FlowLayoutPanel preparationNoteRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        private readonly TextBox preparationNote = new TextBox();
        private readonly Label recentSummary = new Label();
        private Font chosenFont;
        private readonly Label state = new Label(), obsState = new Label(), resultTarget = new Label();
        private readonly TextBox bits = new TextBox();
        private readonly Button saveBits = new Button();
        private readonly ObsRecordingMonitor obs;
        private readonly System.Windows.Forms.Timer obsTimer = new System.Windows.Forms.Timer();
        private readonly object observationGate = new object();
        private ObsObservation latestObservation = new ObsObservation { AtUtc = DateTime.UtcNow, State = "UNKNOWN", Detail = "OBS not connected" };
        private List<ObsObservation> observations;
        private ExperimentProfile edited;
        private CancellationTokenSource cancellation;
        private bool running, closing, initializing = true, refreshingObs, connectingObs, editingResult, resultHasPreparation;
        private string lastPath, resultPath, autoSavePath;
        private readonly PendingTrialStore pending;
        private sealed class ProfileChoice
        {
            public string Path, Label;
            public override string ToString() { return Label; }
        }

        public NoticeForm(string directory, bool test)
        {
            root = directory; smoke = test;
            pending = new PendingTrialStore(root);
            obs = new ObsRecordingMonitor(Path.Combine(root, "obs-recordings"));
            obs.Changed += ObserveChange;
            Text = "통합 대응 실험 · v" + ExperimentPlan.Version;
            Font = new Font("맑은 고딕", 9F);
            chosenFont = new Font(Font, FontStyle.Bold);
            ClientSize = new Size(1040, Math.Min(960, Screen.PrimaryScreen.WorkingArea.Height - 60)); MinimumSize = new Size(940, 700);
            StartPosition = FormStartPosition.CenterScreen;
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            Controls.Add(page);
            settingsPanel.FlowDirection = FlowDirection.TopDown; settingsPanel.WrapContents = false;
            settingsPanel.AutoSize = true; settingsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            page.Controls.Add(settingsPanel);
            var first = Row(settingsPanel);
            profiles.DropDownStyle = ComboBoxStyle.DropDownList; profiles.Width = 320; profiles.DropDownWidth = 800;
            first.Controls.Add(profiles);
            AddButton(first, "프로필 열기", () => Guard(OpenProfile));
            AddButton(first, "다른 이름 저장", () => Guard(SaveProfileAs));
            AddButton(first, "문구 감지 설정", () => Guard(EditDetector));
            settingsPanel.Controls.Add(new Label { AutoSize = true, Text = "프로필 = 보스·패턴별 실험 설정 묶음 (시각·감지·키·파티 포함). 파티 변경만으로 새 프로필이 생기지는 않습니다." });
            var identity = Row(settingsPanel);
            Field(identity, "보스", boss, 110); Field(identity, "패턴", skill, 140);
            Field(identity, "보스 ID", bossId, 115); Field(identity, "프로필 ID", profileId, 225);
            var team = Row(settingsPanel);
            Field(team, "파티 순서", party, 340); Field(team, "시작 캐릭터", start, 135);
            Field(team, "환경", conditions, 220);
            settingsPanel.Controls.Add(new Label { AutoSize = true, Text = "비채점 보조 입력 · 시작 입력 / 준비 대응 / 중간 보조 입력" });
            settingsPanel.Controls.Add(startAttackSummary);
            settingsPanel.Controls.Add(preparationSummary);
            ConfigurePreparationGrid(preparation, false); settingsPanel.Controls.Add(preparation);
            var auxiliaryTools = Row(settingsPanel);
            auxiliaryTools.Controls.Add(new Label { AutoSize = true, Text = "중간 보조 입력 · 문구 기준 ms · 시각을 입력하고 사용 체크 · 결과 자리 수는 그대로", Margin = new Padding(3, 7, 8, 3) });
            AddButton(auxiliaryTools, "보조 입력 추가", () => Guard(AddAuxiliaryRow));
            AddButton(auxiliaryTools, "선택 행 삭제", () => Guard(() => {
                if (auxiliary.CurrentRow == null || running) return;
                auxiliary.Rows.Remove(auxiliary.CurrentRow); ResizeAuxiliary(auxiliary); RefreshSelection(); QueueSettingsSave();
            }));
            ConfigureAuxiliaryGrid(auxiliary, false); settingsPanel.Controls.Add(auxiliary);
            var options = Row(settingsPanel);
            count.AutoSize = true; count.Margin = new Padding(3, 7, 12, 3);
            Field(options, "결과 동작 수 (진입 포함)", count, 55);
            options.Controls.Add(new Label { AutoSize = true, Margin = new Padding(15, 7, 0, 0), Text = "각 행에서 실행할 셀 선택 · 녹색 강조 = 실행값" });
            timing.Width = 945; timing.Height = 220; timing.AllowUserToAddRows = false; timing.AllowUserToDeleteRows = false;
            timing.RowHeadersVisible = false; timing.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            timing.AllowUserToResizeRows = false; timing.SelectionMode = DataGridViewSelectionMode.CellSelect;
            timing.Columns.Add(new DataGridViewTextBoxColumn { Name = "label", HeaderText = "대응", FillWeight = 150 });
            var group = new DataGridViewTextBoxColumn { Name = "group", Visible = false }; timing.Columns.Add(group);
            foreach (string header in new[] { "이른 값", "기준값", "늦은 값" }) timing.Columns.Add(header, header + " ms");
            timing.Columns.Add(new DataGridViewTextBoxColumn { Name = "selected", HeaderText = "이번 입력 ms", ReadOnly = true });
            var actionKey = new DataGridViewComboBoxColumn { Name = "key", HeaderText = "키", FillWeight = 60 };
            actionKey.Items.AddRange("RMB", "Space"); timing.Columns.Add(actionKey); actionKey.DisplayIndex = 1;
            timing.Columns.Add(new DataGridViewTextBoxColumn { Name = "hold", HeaderText = "유지 ms", ReadOnly = true, FillWeight = 65 });
            foreach (DataGridViewColumn column in timing.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            timing.MultiSelect = false;
            timing.EditMode = DataGridViewEditMode.EditProgrammatically;
            timing.DefaultCellStyle.BackColor = Color.FromArgb(255, 253, 235);
            timing.Columns["selected"].DefaultCellStyle.BackColor = SystemColors.Control;
            settingsPanel.Controls.Add(timing);
            var adjustment = Row(settingsPanel);
            ConfigureButton(minus, "− 50 ms"); ConfigureButton(plus, "+ 50 ms");
            minus.Click += (s, e) => Guard(() => AdjustSelected(-50));
            plus.Click += (s, e) => Guard(() => AdjustSelected(50));
            adjustment.Controls.Add(minus); adjustment.Controls.Add(plus);
            editTarget.AutoSize = true; editTarget.Margin = new Padding(8, 7, 0, 0); adjustment.Controls.Add(editTarget);
            settingsState.AutoSize = true; settingsState.Text = "설정은 자동으로 기억합니다. 회차 결과 보관과는 별개입니다."; settingsPanel.Controls.Add(settingsState);
            settingsPanel.Controls.Add(instructionNote);
            live.AutoSize = true; live.Text = "실제 게임에 입력 보내기 (해제하면 관찰만)"; settingsPanel.Controls.Add(live);
            var obsRow = Row(page);
            obsState.Width = 660; obsState.Height = 30; obsState.Text = "현재 녹화 OFF · OBS 미연결은 OFF로 취급"; obsRow.Controls.Add(obsState);
            AddButton(obsRow, "OBS 연결 설정", async () => { try { if (ExperimentSettings.Edit(this, root)) await ConnectObs(); } catch (Exception e) { state.Text = "OBS 설정 오류: " + e.Message; } });
            AddButton(obsRow, "다시 연결", async () => await ConnectObs());
            state.Width = 945; state.Height = 38; state.ForeColor = Color.DarkBlue; page.Controls.Add(state);
            resultTarget.Width = 945; resultTarget.Height = 38; resultTarget.Text = "결과 입력: 회차 종료 후 필요한 동작 수가 표시됩니다. 0 실패 / 1 성공 · 첫 자리는 진입"; page.Controls.Add(resultTarget);
            var outcomes = Row(page);
            bits.Width = 210; bits.Enabled = false; outcomes.Controls.Add(bits);
            ConfigureButton(saveBits, "결과 확정·보관"); saveBits.Enabled = false;
            saveBits.Click += (s, e) => Guard(SaveResult); outcomes.Controls.Add(saveBits);
            AddButton(outcomes, "최근 회차 정정", () => Guard(OpenResult));
            AddButton(outcomes, "기록 폴더", () => Guard(() => { if (smoke) return; string path = Path.Combine(root, "notice-logs"); Directory.CreateDirectory(path); System.Diagnostics.Process.Start("explorer.exe", path); }));
            AddButton(outcomes, "중단 (F9)", () => { if (cancellation != null) cancellation.Cancel(); });
            Field(preparationNoteRow, "준비 중 피격·경직 등 (문제 있을 때만)", preparationNote, 560);
            preparationNote.MaxLength = 500; preparationNote.Enabled = false; preparationNoteRow.Visible = false;
            page.Controls.Add(preparationNoteRow);
            recentSummary.AutoSize = true; recentSummary.MaximumSize = new Size(945, 0);
            recentSummary.Text = "최근 보관 회차 · 아직 결과를 보관한 회차가 없습니다.";
            page.Controls.Add(recentSummary);
            recentTiming.Width = 945; recentTiming.Height = 155; recentTiming.ReadOnly = true;
            recentTiming.AllowUserToAddRows = recentTiming.AllowUserToDeleteRows = false;
            recentTiming.RowHeadersVisible = false; recentTiming.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            recentTiming.BackgroundColor = SystemColors.Control; recentTiming.AllowUserToResizeRows = false;
            foreach (string header in new[] { "대응", "키", "실행 설정 ms", "유지 ms" }) recentTiming.Columns.Add(header, header);
            foreach (DataGridViewColumn column in recentTiming.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            page.Controls.Add(recentTiming);
            page.Controls.Add(recentPreparationTitle);
            ConfigurePreparationGrid(recentPreparation, true); page.Controls.Add(recentPreparation);
            page.Controls.Add(recentAuxiliaryTitle);
            ConfigureAuxiliaryGrid(recentAuxiliary, true); page.Controls.Add(recentAuxiliary);
            LoadEditor(); ReloadProfiles(); initializing = false; RefreshSelection(); RefreshRecentTrial();
            timing.CellMouseClick += (s, e) => {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || running) return;
                Guard(() => {
                    if (e.ColumnIndex >= 2 && e.ColumnIndex <= 4) SelectTimingCell(e.RowIndex, e.ColumnIndex);
                    else if (timing.Columns[e.ColumnIndex].Name == "key") timing.BeginEdit(true);
                });
            };
            timing.CellDoubleClick += (s, e) => {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 2 && e.ColumnIndex <= 4 && !running)
                    Guard(() => { SelectTimingCell(e.RowIndex, e.ColumnIndex); timing.BeginEdit(true); });
            };
            timing.KeyDown += (s, e) => {
                var cell = timing.CurrentCell;
                if (cell == null || running || timing.IsCurrentCellInEditMode) return;
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F2 || e.KeyCode == Keys.Space)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    Guard(() => {
                        if (cell.ColumnIndex >= 2 && cell.ColumnIndex <= 4) SelectTimingCell(cell.RowIndex, cell.ColumnIndex);
                        if (e.KeyCode != Keys.Space || timing.Columns[cell.ColumnIndex].Name == "key") timing.BeginEdit(true);
                    });
                }
            };
            timing.CellValueChanged += (s, e) => { RefreshSelection(); QueueSettingsSave(); };
            timing.CellEndEdit += (s, e) => QueueSettingsSave();
            preparation.CellValueChanged += (s, e) => { RefreshPreparationPreview(); QueueSettingsSave(); };
            preparation.CellEndEdit += (s, e) => { RefreshPreparationPreview(); RefreshSelection(); QueueSettingsSave(); };
            preparation.DataError += (s, e) => { e.ThrowException = false; state.Text = "준비 시각·유지 시간은 50ms 단위 숫자로 입력하세요."; };
            auxiliary.CellValueChanged += (s, e) => QueueSettingsSave();
            auxiliary.CellEndEdit += (s, e) => { RefreshSelection(); QueueSettingsSave(); };
            auxiliary.CurrentCellDirtyStateChanged += (s, e) => {
                if (auxiliary.IsCurrentCellDirty && (auxiliary.CurrentCell is DataGridViewCheckBoxCell || auxiliary.CurrentCell is DataGridViewComboBoxCell))
                    auxiliary.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            auxiliary.DataError += (s, e) => { e.ThrowException = false; state.Text = "보조 입력의 키와 50ms 단위 시각·유지 시간을 확인하세요."; };
            timing.CurrentCellChanged += (s, e) => RefreshAdjustment();
            foreach (var box in new[] { profileId, bossId, boss, skill, conditions, party, start }) box.TextChanged += (s, e) => QueueSettingsSave();
            settingsTimer.Interval = 600;
            settingsTimer.Tick += (s, e) => {
                settingsTimer.Stop();
                if (running || initializing) return;
                if ((timing.IsCurrentCellInEditMode && timing.IsCurrentCellDirty) || (preparation.IsCurrentCellInEditMode && preparation.IsCurrentCellDirty) || (auxiliary.IsCurrentCellInEditMode && auxiliary.IsCurrentCellDirty)) { settingsTimer.Start(); return; }
                try { PersistEditor(); } catch (Exception error) { settingsState.Text = "설정 미저장 · " + error.Message; }
            };
            RefreshAdjustment();
            timing.CurrentCellDirtyStateChanged += (s, e) => { if (timing.IsCurrentCellDirty && timing.CurrentCell is DataGridViewComboBoxCell) timing.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            timing.DataError += (s, e) => { e.ThrowException = false; state.Text = "대응 설정 값을 확인하세요."; };
            profiles.SelectedIndexChanged += (s, e) => { if (!initializing && profiles.SelectedItem != null) Guard(() => SwitchProfile(((ProfileChoice)profiles.SelectedItem).Path)); };
            Shown += async (s, e) => {
                if (smoke) return;
                if (!Native.RegisterHotKey(Handle, 8, 0x4000, 0x77) || !Native.RegisterHotKey(Handle, 9, 0x4000, 0x78))
                { Native.UnregisterHotKey(Handle, 8); Native.UnregisterHotKey(Handle, 9); MessageBox.Show("F8/F9 등록 실패. 기존 실험기를 닫고 다시 실행하세요."); Close(); return; }
                try { pending.DiscardAbandoned(); } catch (Exception error) { state.Text = "이전 임시 기록 정리 실패: " + error.Message; }
                await ConnectObs(); obsTimer.Start();
            };
            obsTimer.Interval = 2000;
            obsTimer.Tick += (s, e) => RefreshObs();
            FormClosing += (s, e) => {
                if (running) { closing = true; cancellation.Cancel(); e.Cancel = true; return; }
                if (!smoke) { try { PersistEditor(); } catch (Exception error) { e.Cancel = MessageBox.Show("설정을 저장하지 못했습니다: " + error.Message + "\n저장하지 않고 닫을까요?", "설정 확인", MessageBoxButtons.YesNo) != DialogResult.Yes; } }
                if (!e.Cancel) { try { pending.Discard(); } catch (Exception error) { state.Text = "임시 회차 삭제 실패: " + error.Message; e.Cancel = true; } }
            };
            FormClosed += (s, e) => { obsTimer.Stop(); obsTimer.Dispose(); obs.Dispose(); if (!smoke) { Native.UnregisterHotKey(Handle, 8); Native.UnregisterHotKey(Handle, 9); } };
        }
        private static FlowLayoutPanel Row(Control parent)
        {
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 5) }; parent.Controls.Add(row); return row;
        }
        private static void ConfigureAuxiliaryGrid(DataGridView grid, bool readOnly)
        {
            grid.Width = 945; grid.ReadOnly = readOnly; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.AllowUserToResizeRows = false; grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "사용", FillWeight = 45 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "label", HeaderText = "보조 입력 이름", FillWeight = 180 });
            var key = new DataGridViewComboBoxColumn { Name = "key", HeaderText = "키", FillWeight = 60 };
            key.Items.AddRange("Space", "RMB", "LMB"); grid.Columns.Add(key);
            grid.Columns.Add("at", "문구 기준 ms"); grid.Columns.Add("hold", "유지 ms");
            foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            ResizeAuxiliary(grid);
        }
        private static void ResizeAuxiliary(DataGridView grid)
        {
            grid.Height = Math.Min(180, grid.ColumnHeadersHeight + Math.Max(1, grid.Rows.Count) * grid.RowTemplate.Height + 4);
        }
        private static void ShowAuxiliaryRows(DataGridView grid, AuxiliaryPress[] presses)
        {
            grid.Rows.Clear();
            foreach (var press in presses ?? new AuxiliaryPress[0])
            {
                int i = grid.Rows.Add(press.Enabled, press.Label, press.Key, press.AtMs, press.HoldMs);
                grid.Rows[i].Tag = press.Id;
            }
            ResizeAuxiliary(grid); grid.ClearSelection();
        }
        private void AddAuxiliaryRow()
        {
            if (running) return;
            if (auxiliary.Rows.Count >= 32) throw new ArgumentException("보조 입력은 최대 32개입니다.");
            int i = auxiliary.Rows.Add(false, "보조 입력 " + (auxiliary.Rows.Count + 1), "Space", "", 100);
            auxiliary.Rows[i].Tag = "aux-" + Guid.NewGuid().ToString("N");
            ResizeAuxiliary(auxiliary); auxiliary.CurrentCell = auxiliary.Rows[i].Cells["at"]; auxiliary.BeginEdit(true);
            settingsState.Text = "설정 미저장 · 문구 기준 시각을 입력한 뒤 사용할 행을 체크하세요.";
        }
        private static void ConfigurePreparationGrid(DataGridView grid, bool readOnly)
        {
            grid.Width = 945; grid.ReadOnly = readOnly; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.AllowUserToResizeRows = false; grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "label", HeaderText = "준비 대응", ReadOnly = true, FillWeight = 180 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", HeaderText = "키", ReadOnly = true, FillWeight = 55 });
            grid.Columns.Add("at", "시작 ms"); grid.Columns.Add("hold", "유지 ms");
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "end", HeaderText = "해제 ms", ReadOnly = true });
            foreach (DataGridViewColumn column in grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
                column.DefaultCellStyle.BackColor = readOnly || column.ReadOnly ? SystemColors.Control : Color.FromArgb(255, 253, 235);
            }
        }
        private static void ShowPreparationRows(DataGridView grid, PreparationPress[] values)
        {
            grid.Rows.Clear();
            foreach (var p in values ?? new PreparationPress[0]) grid.Rows.Add(p.Label, p.Key, p.AtMs, p.HoldMs, (p.AtMs + p.HoldMs).ToString("0"));
            grid.Height = Math.Min(220, grid.ColumnHeadersHeight + grid.Rows.Count * grid.RowTemplate.Height + 4);
            grid.Visible = grid.Rows.Count > 0; grid.ClearSelection();
        }
        private void RefreshPreparationPreview()
        {
            if (initializing || running) return;
            bool previous = initializing; initializing = true;
            try
            {
                foreach (DataGridViewRow row in preparation.Rows)
                {
                    double at, hold;
                    row.Cells["end"].Value = Double.TryParse(Convert.ToString(row.Cells["at"].Value), out at)
                        && Double.TryParse(Convert.ToString(row.Cells["hold"].Value), out hold)
                        && !Double.IsNaN(at + hold) && !Double.IsInfinity(at + hold) ? (at + hold).ToString("0") : "확인 필요";
                }
            }
            finally { initializing = previous; }
        }
        private static void Field(Control row, string title, Control control, int width)
        {
            row.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(3, 7, 4, 3) }); control.Width = width; row.Controls.Add(control);
        }
        private static void AddButton(Control row, string title, Action action)
        {
            var button = new Button(); ConfigureButton(button, title); button.Click += (s, e) => action(); row.Controls.Add(button);
        }
        private void Guard(Action action) { try { action(); } catch (Exception e) { state.Text = "설정/저장 오류: " + e.Message; } }
        private static void ConfigureButton(Button button, string title)
        {
            button.Text = title; button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(110, 32); button.Padding = new Padding(12, 3, 12, 3);
        }
        private void QueueSettingsSave()
        {
            if (initializing || running || smoke) return;
            settingsState.Text = "설정 변경됨 · 유효한 입력을 마치면 자동 저장합니다.";
            settingsTimer.Stop(); settingsTimer.Start();
        }
        private void RefreshAdjustment()
        {
            var cell = timing.CurrentCell;
            bool editable = cell != null && cell.ColumnIndex >= 2 && cell.ColumnIndex <= 4;
            minus.Enabled = plus.Enabled = editable;
            editTarget.Text = editable ? Convert.ToString(timing.Rows[cell.RowIndex].Cells[0].Value) + " · " + timing.Columns[cell.ColumnIndex].HeaderText + " 선택됨"
                : "클릭: 실행값 선택 · 두 번 클릭/Enter: 숫자 편집";
        }
        private void AdjustSelected(int amount)
        {
            if (running) return;
            var cell = timing.CurrentCell;
            if (cell == null || cell.ColumnIndex < 2 || cell.ColumnIndex > 4) return;
            SelectTimingCell(cell.RowIndex, cell.ColumnIndex);
            timing.EndEdit();
            double value = Number(timing.Rows[cell.RowIndex], cell.ColumnIndex) + amount;
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 100 || value > 120000 || value % 50 != 0)
                throw new ArgumentException("시각은 100~120000ms 내 50ms 단위로 입력하세요.");
            cell.Value = value; RefreshSelection(); QueueSettingsSave();
        }
        private void LoadEditor()
        {
            initializing = true; edited = ProfileStore.Snapshot(); autoSavePath = null;
            bool automaticStart = edited.StartAttack != null;
            startAttackSummary.Visible = automaticStart;
            startAttackSummary.Text = automaticStart
                ? "시작 입력 · F8 기준 · " + edited.StartAttack.Key + " " + edited.StartAttack.AtMs.Length + "회 자동 → 문구 감지 → 준비·진입·후속 자동\n"
                  + "시각 " + String.Join(" / ", edited.StartAttack.AtMs.Select(at => at.ToString("0")))
                  + "ms · 각 " + edited.StartAttack.HoldMs.ToString("0") + "ms 유지 · 시작 키를 직접 누르지 마세요."
                : "";
            bool automaticPreparation = edited.Preparation != null && edited.Preparation.Length > 0;
            preparationSummary.Visible = automaticPreparation;
            preparationSummary.Text = automaticPreparation
                ? "준비 대응 · 문구 기준 · 회피 " + edited.Preparation.Count(p => p.Key == "RMB") + "회"
                  + (edited.Preparation.Any(p => p.Key != "RMB") ? "와 방향 이동" : edited.AllowManualMovement ? " · WASD 직접 이동 가능" : " · 방향 입력 없음") + " · 시작·유지 시간은 50ms 단위로 조정\n"
                  + "진입 전 준비 후보입니다. 아래 결과 동작 수와 성공·실패 " + edited.Actions.Length + "자리에는 포함하지 않습니다."
                : "";
            ShowPreparationRows(preparation, edited.Preparation);
            ShowAuxiliaryRows(auxiliary, edited.AuxiliaryInputs);
            string startControls = edited.StartAttack != null && edited.StartAttack.Key == "E" ? "LMB·RMB·Space·E" : "LMB·RMB·Space";
            instructionNote.Text = "시각은 문구 감지 기준 · 50ms 단위. F8: 문구 출현 전 게임에서 시작 / F9: 상시 중단\n"
                + (automaticStart
                    ? (edited.AllowManualMovement
                        ? "WASD 직접 이동 가능 · " + startControls + "는 놓으세요. 시작 입력·회피·교대는 자동입니다.\n"
                        : "F8 전에 WASD·" + startControls + "를 놓으세요. 시작 입력부터 자동이며 결과는 진입 이후만 기록합니다.\n")
                      + "시작 " + edited.StartAttack.Key + " " + edited.StartAttack.AtMs.Length + "회 해제 전 나타난 문구가 감지 확정되면 중단합니다.\n" + edited.Conditions
                    : automaticPreparation
                    ? (edited.AllowManualMovement
                        ? "WASD 직접 이동 가능 · RMB·Space는 놓으세요. 준비 대응부터 자동 실행합니다.\n"
                        : "준비 대응부터 자동 실행합니다. F8 전에 WASD·RMB·Space를 놓고 이후 수동 대응을 추가하지 마세요.\n")
                      + "준비는 미검증 후보입니다. 결과 0/1은 진입 이후만 기록하며 준비 중 피격·경직은 별도로 확인합니다.\n" + edited.Conditions
                    : edited.ManualPreparationRmbUntilMs > 0
                    ? "이 프로필은 감지 확정 후부터 감지 기준 " + edited.ManualPreparationRmbUntilMs + "ms 미만까지 준비 RMB를 허용합니다.\n"
                      + "준비 종료 전에 RMB를 놓으세요. 그 이후 RMB·Space, 준비 중 Space·보조키, 창 이탈은 중단합니다.\n"
                      + "이 시각은 프로그램 정책이며 회피 성공 폭이 아닙니다. 수동 준비 동작은 자동 기록하지 않습니다.\n" + edited.Conditions
                    : (edited.AllowManualMovement ? "WASD 직접 이동 가능 · RMB·Space는 놓으세요.\n" : "")
                      + "창 이탈·수동 대응 입력 시 중단합니다. 동작 수는 선택한 보스·패턴에 따라 정해집니다.\n" + edited.Conditions);
            profileId.Text = edited.Id; bossId.Text = edited.BossId; boss.Text = edited.BossLabel; skill.Text = edited.Skill;
            string[] legacy = edited.Conditions.Split('/');
            party.Text = edited.Party ?? (legacy.Length > 1 ? legacy[1].Trim() : "");
            start.Text = edited.StartCharacter ?? (legacy.Length > 2 ? legacy[2].Trim().Replace("시작 ", "") : "");
            conditions.Text = edited.Party == null && legacy.Length > 1 ? legacy[0].Trim() : edited.Conditions;
            count.Text = edited.Actions.Length + "개";
            timing.Rows.Clear();
            foreach (var a in edited.Actions) timing.Rows.Add(a.Label, a.Group, a.Timing.EarlyMs, a.Timing.BaselineMs, a.Timing.LateMs, "", a.Key, a.HoldMs);
            for (int i = 0; i < edited.Actions.Length; i++) timing.Rows[i].Tag = edited.Actions[i].SelectedTiming;
            instructionNote.Text += "\n사용 체크한 보조 입력도 문구 기준으로 실행합니다. " + startControls + "를 놓고 시작하세요. 보조 전송은 게임 성공 판정이 아닙니다.";
            timing.ClearSelection(); live.Checked = false; initializing = false;
        }
        private ExperimentProfile ReadEditor()
        {
            if (!timing.EndEdit() || !preparation.EndEdit() || !auxiliary.EndEdit()) throw new ArgumentException("편집 중인 숫자나 키를 확인하세요.");
            if (timing.Rows.Count != edited.Actions.Length) throw new InvalidOperationException("프로필의 동작 수와 화면이 일치하지 않습니다. 프로필을 다시 선택하세요.");
            if (preparation.Rows.Count != (edited.Preparation == null ? 0 : edited.Preparation.Length)) throw new InvalidOperationException("프로필의 준비 대응 수와 화면이 일치하지 않습니다. 프로필을 다시 선택하세요.");
            var p = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ExperimentProfile>(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(edited));
            p.Id = profileId.Text.Trim(); p.BossId = bossId.Text.Trim(); p.BossLabel = boss.Text.Trim(); p.Skill = skill.Text.Trim(); p.Conditions = conditions.Text.Trim();
            p.Party = party.Text.Trim(); p.StartCharacter = start.Text.Trim();
            p.Actions = timing.Rows.Cast<DataGridViewRow>().Select((row, i) => new ActionProfile {
                Id = edited.Actions[i].Id,
                Label = Convert.ToString(row.Cells[0].Value), Group = edited.Actions[i].Group, Key = Convert.ToString(row.Cells["key"].Value), SelectedTiming = Convert.ToString(row.Tag),
                HoldMs = edited.Actions[i].HoldMs, MinimumMs = edited.Actions[i].MinimumMs, MaximumMs = edited.Actions[i].MaximumMs, Timing = new TimingCandidate { EarlyMs = Number(row, 2), BaselineMs = Number(row, 3), LateMs = Number(row, 4) }
            }).ToArray();
            if (edited.Preparation != null)
                p.Preparation = preparation.Rows.Cast<DataGridViewRow>().Select((row, i) => new PreparationPress {
                    Id = edited.Preparation[i].Id, Label = edited.Preparation[i].Label, Key = edited.Preparation[i].Key,
                    AtMs = Number(row, 2), HoldMs = Number(row, 3)
                }).ToArray();
            if (auxiliary.Rows.Count > 0 || edited.AuxiliaryInputs != null)
                p.AuxiliaryInputs = auxiliary.Rows.Cast<DataGridViewRow>().Select(row => new AuxiliaryPress {
                    Id = Convert.ToString(row.Tag), Label = Convert.ToString(row.Cells["label"].Value),
                    Key = Convert.ToString(row.Cells["key"].Value), Enabled = Convert.ToBoolean(row.Cells["enabled"].Value),
                    AtMs = Number(row, 3), HoldMs = Number(row, 4)
                }).ToArray();
            if (p.AuxiliaryInputs != null && p.AuxiliaryInputs.Length > 0) p.SchemaVersion = 2;
            ProfileStore.Validate(p); return p;
        }
        private static double Number(DataGridViewRow row, int column)
        {
            double value;
            if (!Double.TryParse(Convert.ToString(row.Cells[column].Value), out value)) throw new ArgumentException("시각에는 숫자를 입력하세요.");
            return value;
        }
        private void SelectTimingCell(int row, int column)
        {
            if (running || initializing || row < 0 || column < 2 || column > 4) return;
            if (!timing.EndEdit()) throw new ArgumentException("편집 중인 숫자를 확인하세요.");
            timing.Rows[row].Tag = ExperimentPlan.TimingIds[column - 2];
            RefreshSelection(); QueueSettingsSave();
        }
        private void RefreshSelection()
        {
            if (initializing || running) return;
            try
            {
                initializing = true;
                var p = ReadEditor();
                double previous = 0;
                for (int i = 0; i < p.Actions.Length; i++)
                {
                    int column = ExperimentPlan.TimingColumn(p.Actions[i].SelectedTiming);
                    var t = p.Actions[i].Timing;
                    double selected = new[] { t.EarlyMs, t.BaselineMs, t.LateMs }[column];
                    timing.Rows[i].Cells[5].Value = selected.ToString("0");
                    for (int c = 2; c <= 4; c++)
                    {
                        var cell = timing.Rows[i].Cells[c]; bool chosen = c == column + 2;
                        cell.Style.BackColor = chosen ? Color.FromArgb(210, 238, 210) : Color.FromArgb(255, 253, 235);
                        cell.Style.SelectionBackColor = cell.Style.BackColor; cell.Style.SelectionForeColor = Color.Black;
                        cell.Style.Font = chosen ? chosenFont : timing.Font;
                    }
                    if (i == 0 && p.ManualPreparationRmbUntilMs > 0 && selected <= p.ManualPreparationRmbUntilMs)
                        throw new ArgumentException("이번 조합의 첫 자동 입력은 수동 준비 RMB 종료 시각 뒤여야 합니다.");
                    if (i > 0 && selected - previous < p.Actions[i - 1].HoldMs) throw new ArgumentException("이번 조합의 입력 순서 또는 키 유지 시간이 겹칩니다.");
                    previous = selected;
                }
                state.Text = "대기 · 진입 포함 " + p.Actions.Length + "개 동작. 게임에서 F8을 누르세요.";
            }
            catch (Exception e) { state.Text = "설정 확인: " + e.Message; }
            finally { initializing = false; }
        }
        private void PersistEditor(string protectedPath = null)
        {
            settingsTimer.Stop();
            ProfileStore.Apply(ReadEditor());
            settingsTimer.Stop();
            if (smoke) return;
            string managed = Path.GetFullPath(Path.Combine(root, "profiles")) + Path.DirectorySeparatorChar;
            string destination = autoSavePath;
            if (destination == null)
            {
                destination = ProfileStore.SourcePath.StartsWith(managed, StringComparison.OrdinalIgnoreCase)
                    ? ProfileStore.SourcePath : Path.GetFullPath(Path.Combine(managed, Path.GetFileName(ProfileStore.SourcePath)));
                if (File.Exists(destination) && !String.Equals(destination, ProfileStore.SourcePath, StringComparison.OrdinalIgnoreCase))
                    destination = Path.Combine(managed, ProfileStore.Current.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            }
            if (String.Equals(destination, protectedPath, StringComparison.OrdinalIgnoreCase))
                destination = Path.Combine(managed, ProfileStore.Current.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            ProfileStore.SaveCopy(destination);
            autoSavePath = destination;
            File.WriteAllText(Path.Combine(root, "last-profile.txt"), ProfileStore.SourcePath);
            edited = ProfileStore.Snapshot(); settingsState.Text = "설정 자동 저장됨 · " + DateTime.Now.ToString("HH:mm:ss");
            ReloadProfiles();
        }
        private void ReloadProfiles()
        {
            initializing = true; profiles.Items.Clear();
            string managed = Path.GetFullPath(Path.Combine(root, "profiles"));
            string bundled = Path.GetFullPath(Path.Combine(root, "..", "..", "tools", "experiment", "profiles"));
            var paths = new List<string> { ProfileStore.SourcePath };
            foreach (string folder in new[] { Path.Combine(root, "profiles"), Path.Combine(root, "..", "..", "tools", "experiment", "profiles"), Path.GetDirectoryName(ProfileStore.SourcePath) })
                if (Directory.Exists(folder)) paths.AddRange(Directory.GetFiles(folder, "*.json"));
            foreach (string path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var p = ProfileStore.Parse(File.ReadAllText(path));
                    string saved = Path.Combine(managed, Path.GetFileName(path));
                    if (String.Equals(Path.GetDirectoryName(path), bundled, StringComparison.OrdinalIgnoreCase)
                        && !String.Equals(path, ProfileStore.SourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(saved))
                    {
                        try
                        {
                            var copy = ProfileStore.Parse(File.ReadAllText(saved));
                            if (copy.Id == p.Id && copy.BossId == p.BossId && copy.Skill == p.Skill) continue;
                        }
                        catch (Exception) { /* Invalid saved copy must not hide the bundled profile. */ }
                    }
                    profiles.Items.Add(new ProfileChoice { Path = path, Label = p.BossLabel + " / " + p.Skill
                        + (String.Equals(Path.GetDirectoryName(path), managed, StringComparison.OrdinalIgnoreCase) ? " (저장본)" : "")
                        + " · " + Path.GetFileNameWithoutExtension(path) });
                }
                catch (Exception) { }
            }
            profiles.SelectedIndex = profiles.Items.Count == 0 ? -1 : 0; initializing = false;
        }
        private void SwitchProfile(string path)
        {
            if (String.Equals(path, ProfileStore.SourcePath, StringComparison.OrdinalIgnoreCase)) return;
            PersistEditor(path); ProfileStore.Load(path); LoadEditor(); ReloadProfiles(); RefreshSelection();
        }
        private void OpenProfile()
        {
            using (var dialog = new OpenFileDialog { Filter = "실험 프로필 (*.json)|*.json", InitialDirectory = Path.GetDirectoryName(ProfileStore.SourcePath) })
                if (dialog.ShowDialog(this) == DialogResult.OK) SwitchProfile(dialog.FileName);
        }
        private void SaveProfileAs()
        {
            PersistEditor();
            using (var dialog = new SaveFileDialog { Filter = "실험 프로필 (*.json)|*.json", FileName = profileId.Text + ".json", InitialDirectory = Path.Combine(root, "profiles") })
                if (dialog.ShowDialog(this) == DialogResult.OK) SaveProfileCopy(dialog.FileName);
        }
        private void SaveProfileCopy(string path)
        {
            ProfileStore.Apply(ReadEditor()); ProfileStore.SaveCopy(path); autoSavePath = ProfileStore.SourcePath;
            edited = ProfileStore.Snapshot();
            File.WriteAllText(Path.Combine(root, "last-profile.txt"), ProfileStore.SourcePath);
            ReloadProfiles(); settingsState.Text = "별도 설정 저장됨 · " + Path.GetFileName(path);
        }
        private void EditDetector()
        {
            PersistEditor();
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var detector = serializer.Deserialize<DetectorProfile>(serializer.Serialize(edited.Detector));
            using (var dialog = new Form { Text = "문구 감지 설정 · 새 보스의 템플릿은 별도 검증 필요", ClientSize = new Size(590, 550), StartPosition = FormStartPosition.CenterParent })
            {
                var grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = detector, HelpVisible = true };
                var save = new Button { Text = "적용", Dock = DockStyle.Bottom, Height = 35, DialogResult = DialogResult.OK };
                dialog.Controls.Add(grid); dialog.Controls.Add(save);
                if (dialog.ShowDialog(this) == DialogResult.OK) { edited.Detector = detector; QueueSettingsSave(); }
            }
        }
        private async Task ConnectObs()
        {
            if (connectingObs || smoke) return; connectingObs = true;
            try { var settings = ExperimentSettings.Load(root); await obs.ConnectAsync(settings.Endpoint, settings.Password()); }
            catch (Exception) { if (!IsDisposed) obsState.Text = "현재 녹화 OFF · OBS 미연결은 OFF로 취급"; }
            finally { connectingObs = false; }
        }
        private async void RefreshObs()
        {
            if (refreshingObs || connectingObs || !obs.Connected) return;
            refreshingObs = true;
            try { await obs.RefreshAsync(); } catch (Exception) { } finally { refreshingObs = false; }
        }
        private ObsObservation FreshObservation()
        {
            // The receive callback publishes a private immutable snapshot. F8 and
            // the timed worker do not wait on the monitor's ledger/file-write lock.
            var snapshot = Volatile.Read(ref latestObservation).Copy();
            if ((snapshot.State == "ON" || snapshot.State == "OFF" || snapshot.State == "PAUSED") && DateTime.UtcNow - snapshot.AtUtc > TimeSpan.FromSeconds(3))
                return new ObsObservation { AtUtc = DateTime.UtcNow, State = "UNKNOWN", Detail = "OBS status has not been refreshed for over 3 seconds" };
            return snapshot;
        }
        private void ObserveChange()
        {
            var snapshot = obs.Snapshot();
            Volatile.Write(ref latestObservation, snapshot);
            lock (observationGate) { if (observations != null) observations.Add(snapshot); }
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(() => {
                if (!IsDisposed) obsState.Text = snapshot.State == "ON" ? "현재 녹화 ON · 회차의 녹화 여부는 F8 때 고정합니다."
                    : snapshot.State == "OFF" ? "현재 녹화 OFF · 저장 중인 회차의 F8 상태는 바뀌지 않습니다."
                    : snapshot.State == "PAUSED" ? "현재 녹화 OFF · 일시정지 상태"
                    : "현재 녹화 OFF · OBS 미연결·미확인은 OFF로 취급";
            })); } catch (InvalidOperationException) { }
        }
        private void SetSettingsEnabled(bool enabled) { settingsPanel.Enabled = enabled; }
        private NoticeRecord CreateRecord(ObsObservation recordingAtF8 = null, DateTime? f8Utc = null)
        {
            ProfileStore.Apply(ReadEditor());
            string id = ExperimentPlan.PerActionPattern;
            var record = new NoticeRecord { Mode = live.Checked ? ExperimentPlan.LiveMode : "observe", Pattern = id, SelectedTimings = ExperimentPlan.SelectedTimings(id), Candidates = ExperimentPlan.Defaults(), DelaysMs = ExperimentPlan.Resolve(ExperimentPlan.Defaults(), id), ObsLedgerDirectory = "../obs-recordings" };
            record.FreezeRecordingAtF8(f8Utc ?? DateTime.UtcNow, recordingAtF8 ?? FreshObservation());
            return record;
        }
        private void SelectResult(string path)
        {
            var record = TrialResultStore.ReadTrial(path); string existing = TrialResultStore.ReadBits(path);
            resultPath = path; bits.Text = existing; bits.Enabled = saveBits.Enabled = true;
            resultHasPreparation = record.ProfileSnapshot != null && record.ProfileSnapshot.Preparation != null && record.ProfileSnapshot.Preparation.Length > 0;
            preparationNoteRow.Visible = resultHasPreparation; preparationNote.Enabled = resultHasPreparation;
            preparationNote.Text = resultHasPreparation ? TrialResultStore.ReadPreparationNote(path) : "";
            resultTarget.Text = (record.ProfileSnapshot == null ? record.BossId : record.ProfileSnapshot.BossLabel) + " / " + record.Skill + " · 시작 " + TrialResultStore.ReadStartCharacter(path) + " · 결과 " + record.ActionIds.Length + "자리 · " +
                (record.RecordingBasis == "f8-snapshot-unknown-off" ? "F8 녹화 " : "구버전 녹화 ") +
                (record.Recording == "UNKNOWN" ? "OFF (미확인→OFF)" : record.Recording) + "\n" +
                (path == pending.CurrentPath ? "임시 회차 · 결과 확정 시 보관 / 미입력 상태로 다음 F8 또는 종료 시 폐기" : "보관된 회차 · 결과를 고치면 같은 회차에 정정합니다.");
        }
        private void SaveResult()
        {
            if (running) throw new InvalidOperationException("회차 종료 후 결과를 입력하세요.");
            string note = resultHasPreparation ? preparationNote.Text : null;
            string saved = resultPath == pending.CurrentPath ? pending.Accept(bits.Text, note) : resultPath;
            if (saved == resultPath) TrialResultStore.Save(saved, bits.Text, note);
            SelectResult(saved); RefreshRecentTrial(); state.Text = "회차 보관 완료 · " + bits.Text;
        }
        private void RefreshRecentTrial()
        {
            recentTiming.Rows.Clear();
            ShowPreparationRows(recentPreparation, null); recentPreparationTitle.Visible = false;
            ShowAuxiliaryRows(recentAuxiliary, null); recentAuxiliary.Visible = recentAuxiliaryTitle.Visible = false;
            try
            {
                string path = TrialCorrectionForm.LatestPath(root);
                var record = TrialResultStore.ReadTrial(path); string results = TrialResultStore.ReadBits(path);
                string note = TrialResultStore.ReadPreparationNote(path);
                if (String.IsNullOrEmpty(results)) throw new ArgumentException("결과를 보관한 회차가 없습니다.");
                DateTime at;
                string when = DateTime.TryParse(record.StartedUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out at) ? at.ToLocalTime().ToString("MM-dd HH:mm:ss") : "시각 미기록";
                string name = record.ProfileSnapshot == null ? record.BossId : record.ProfileSnapshot.BossLabel;
                recentSummary.Text = "최근 보관 회차 · " + when + " · " + name + " / " + record.Skill + " · 결과 " + results
                    + "\n시작 " + TrialResultStore.ReadStartCharacter(path) + " · " + record.Party + " · 녹화 " + record.Recording
                    + (record.ProfileSnapshot != null && record.ProfileSnapshot.StartAttack != null
                        ? " · 시작 " + record.ProfileSnapshot.StartAttack.Key + record.ProfileSnapshot.StartAttack.AtMs.Length + " 자동" : "")
                    + (record.ProfileSnapshot != null && record.ProfileSnapshot.Preparation != null && record.ProfileSnapshot.Preparation.Length > 0 ? " · 자동 준비 포함 (결과는 진입 이후)" : "")
                    + " · 아래 값은 당시 기록입니다."
                    + (!String.IsNullOrEmpty(note) ? "\n준비 메모: " + note : "");
                ShowPreparationRows(recentPreparation, record.ProfileSnapshot == null ? null : record.ProfileSnapshot.Preparation);
                recentPreparationTitle.Visible = recentPreparation.Rows.Count > 0;
                ShowAuxiliaryRows(recentAuxiliary, record.ProfileSnapshot == null ? null : record.ProfileSnapshot.AuxiliaryInputs);
                recentAuxiliary.Visible = recentAuxiliaryTitle.Visible = recentAuxiliary.Rows.Count > 0;
                for (int i = 0; i < record.ActionIds.Length; i++)
                {
                    var action = record.ProfileSnapshot == null || record.ProfileSnapshot.Actions == null ? null
                        : record.ProfileSnapshot.Actions.FirstOrDefault(a => a.Id == record.ActionIds[i]);
                    recentTiming.Rows.Add(action == null ? record.ActionIds[i] : action.Label,
                        record.ActionKeys != null && i < record.ActionKeys.Length ? record.ActionKeys[i] : "미기록",
                        record.DelaysMs != null && i < record.DelaysMs.Length ? record.DelaysMs[i].ToString("0") : "미기록",
                        action == null ? "미기록" : action.HoldMs.ToString("0"));
                }
                recentTiming.ClearSelection();
            }
            catch (Exception e) { recentTiming.Rows.Clear(); ShowPreparationRows(recentPreparation, null); recentPreparationTitle.Visible = false; recentSummary.Text = "최근 보관 회차 · " + e.Message; }
        }
        private void ClearResult()
        {
            resultPath = null; bits.Text = ""; bits.Enabled = saveBits.Enabled = false;
            resultHasPreparation = false; preparationNote.Text = ""; preparationNote.Enabled = false; preparationNoteRow.Visible = false;
            resultTarget.Text = "결과 입력 전에는 임시 회차입니다. 다음 F8 또는 종료 시 미입력 회차는 폐기합니다.";
        }
        private void OpenResult()
        {
            if (running || pending.CurrentPath != null) throw new InvalidOperationException("현재 회차의 결과를 먼저 확정하세요.");
            string path = TrialCorrectionForm.LatestPath(root);
            editingResult = true;
            try
            {
                using (var dialog = new TrialCorrectionForm(path))
                    if (dialog.ShowDialog(this) == DialogResult.OK) { SelectResult(path); RefreshRecentTrial(); state.Text = "최근 회차 정정 완료"; }
            }
            finally { editingResult = false; }
        }
        internal void VerifySmokeSettings()
        {
            string original = ProfileStore.SourcePath; var snapshot = ProfileStore.Snapshot();
            try
            {
                if (live.Checked) throw new Exception("Unsafe startup");
                if (!timing.Columns["hold"].ReadOnly || !ReadEditor().Actions.Select(a => a.HoldMs).SequenceEqual(snapshot.Actions.Select(a => a.HoldMs))
                    || !CreateRecord().ProfileSnapshot.Actions.Select(a => a.HoldMs).SequenceEqual(snapshot.Actions.Select(a => a.HoldMs)))
                    throw new Exception("Action hold lost between editor and record snapshot");
                bool hasPreparation = snapshot.Preparation != null && snapshot.Preparation.Length > 0;
                if (!String.IsNullOrEmpty(preparationSummary.Text) != hasPreparation) throw new Exception("Preparation display differs from profile");
                if (preparation.Rows.Count != (hasPreparation ? snapshot.Preparation.Length : 0)
                    || !preparation.Columns["label"].ReadOnly || !preparation.Columns["key"].ReadOnly
                    || preparation.Columns["at"].ReadOnly || preparation.Columns["hold"].ReadOnly)
                    throw new Exception("Preparation grid rows or editable fields differ from profile");
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                if (ReadEditor().AllowManualMovement != snapshot.AllowManualMovement
                    || CreateRecord().ProfileSnapshot.AllowManualMovement != snapshot.AllowManualMovement
                    || (snapshot.AllowManualMovement && !instructionNote.Text.Contains("WASD 직접 이동 가능")))
                    throw new Exception("Manual movement policy lost between editor, instructions and trial snapshot");
                if (!String.IsNullOrEmpty(startAttackSummary.Text) != (snapshot.StartAttack != null)
                    || serializer.Serialize(ReadEditor().StartAttack) != serializer.Serialize(snapshot.StartAttack)
                    || serializer.Serialize(CreateRecord().ProfileSnapshot.StartAttack) != serializer.Serialize(snapshot.StartAttack))
                    throw new Exception("Start attack display or editor/trial snapshot differs from profile");
                if (serializer.Serialize(ReadEditor().Preparation) != serializer.Serialize(snapshot.Preparation)
                    || serializer.Serialize(CreateRecord().ProfileSnapshot.Preparation) != serializer.Serialize(snapshot.Preparation))
                    throw new Exception("Preparation changed during editor or trial snapshot roundtrip");
                if (timing.Columns.Cast<DataGridViewColumn>().Concat(recentTiming.Columns.Cast<DataGridViewColumn>())
                    .Concat(preparation.Columns.Cast<DataGridViewColumn>()).Concat(recentPreparation.Columns.Cast<DataGridViewColumn>())
                    .Any(c => c.SortMode != DataGridViewColumnSortMode.NotSortable)) throw new Exception("Action rows can be reordered");
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int i = 0; i < timing.Rows.Count; i++) SelectTimingCell(i, 2 + (i + pass) % 3);
                    var record = CreateRecord();
                    for (int i = 0; i < record.ActionIds.Length; i++)
                        if (Convert.ToString(timing.Rows[i].Cells[5].Value) != record.DelaysMs[i].ToString("0") || record.SelectedTimings[i] != Convert.ToString(timing.Rows[i].Tag))
                            throw new Exception("Per-action preview differs from recorded selection");
                }
                timing.Rows[0].Cells["key"].Value = "Space";
                if (CreateRecord().ActionKeys[0] != "Space" || timing.Rows[0].Cells["key"].ReadOnly) throw new Exception("Unified entry key not applied");
                for (int i = 0; i < timing.Rows.Count; i++) SelectTimingCell(i, 3);
                int last = timing.Rows.Count - 1;
                if (last > 0)
                {
                    string originalKey = ReadEditor().Actions[last].Key;
                    timing.Rows[last].Cells["key"].Value = "RMB";
                    if (CreateRecord().ActionKeys[last] != "RMB") throw new Exception("Follow-up key not applied to record");
                    timing.CurrentCell = timing.Rows[last].Cells["key"];
                    if (plus.Enabled || minus.Enabled) throw new Exception("Key cell allows numeric adjustment");
                    timing.Rows[last].Cells["key"].Value = originalKey;
                }
                var before = ReadEditor();
                timing.CurrentCell = timing.Rows[0].Cells[2];
                double early = before.Actions[0].Timing.EarlyMs;
                AdjustSelected(-50); timing.EndEdit();
                var after = ReadEditor();
                if (after.Actions[0].Timing.EarlyMs != early - 50 || after.Actions[0].Timing.BaselineMs != before.Actions[0].Timing.BaselineMs || after.Actions[0].Timing.LateMs != before.Actions[0].Timing.LateMs)
                    throw new Exception("50ms adjustment changed wrong cell");
                for (int i = 1; i < after.Actions.Length; i++)
                    if (after.Actions[i].Timing.EarlyMs != before.Actions[i].Timing.EarlyMs) throw new Exception("Adjustment changed other action");
                AdjustSelected(50);
                timing.CurrentCell = timing.Rows[0].Cells[5];
                if (plus.Enabled || minus.Enabled) throw new Exception("Readonly preview can be adjusted");
                AdjustSelected(50);
                if (ReadEditor().Actions[0].Timing.EarlyMs != early) throw new Exception("Adjustment round trip failed");
                if (timing.AllowUserToAddRows || timing.AllowUserToDeleteRows) throw new Exception("Action count is user-editable");
                foreach (int size in new[] { 1, 12, 1 })
                {
                    var fixture = ProfileStore.Snapshot(); fixture.MinimumSpacingMs = 250; fixture.ManualPreparationRmbUntilMs = 0; fixture.Preparation = null; fixture.AuxiliaryInputs = null;
                    fixture.Actions = Enumerable.Range(0, size).Select(i => new ActionProfile {
                        Id = "fixture-" + i, Label = "동작 " + i, Key = i == 0 ? "RMB" : "Space", Group = i % 2 == 0 ? "A" : "B",
                        MinimumMs = 100, MaximumMs = 120000, Timing = new TimingCandidate { EarlyMs = 1900 + i * 1000, BaselineMs = 2000 + i * 1000, LateMs = 2100 + i * 1000 }
                    }).ToArray();
                    ProfileStore.Apply(fixture); LoadEditor();
                    if (!String.IsNullOrEmpty(preparationSummary.Text) || preparation.Rows.Count != 0) throw new Exception("Preparation display leaked to another profile");
                    var record = CreateRecord();
                    if (count.Text != size + "개" || record.ActionIds.Length != size || !record.DelaysMs.SequenceEqual(fixture.Actions.Select(a => a.Timing.BaselineMs)))
                        throw new Exception("Profile-driven action count or timings changed");
                }
                VerifyAuxiliaryEditor();
                SetSettingsEnabled(false); if (count.Enabled || timing.Enabled || preparation.Enabled || auxiliary.Enabled || party.Enabled || profiles.Enabled) throw new Exception("Settings not locked");
                SetSettingsEnabled(true);
            }
            finally { ProfileStore.Load(original); ProfileStore.Apply(snapshot); LoadEditor(); RefreshSelection(); }
        }
        private void VerifyAuxiliaryEditor()
        {
            var before = ProfileStore.Snapshot();
            string previousState = settingsState.Text;
            try
            {
                AddAuxiliaryRow(); auxiliary.EndEdit();
                var row = auxiliary.Rows[auxiliary.Rows.Count - 1];
                if (Convert.ToBoolean(row.Cells["enabled"].Value)) throw new Exception("New auxiliary input enabled without user selection");
                bool rejected = false;
                try { ReadEditor(); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("Blank auxiliary time accepted");
                row.Cells["at"].Value = 1000; row.Cells["key"].Value = "LMB"; row.Cells["enabled"].Value = true;
                var record = CreateRecord();
                var press = record.ProfileSnapshot.AuxiliaryInputs.Last();
                if (record.ProfileSnapshot.SchemaVersion != 2 || press.Key != "LMB" || press.AtMs != 1000 || !press.Enabled
                    || record.ActionIds.Length != before.Actions.Length)
                    throw new Exception("Auxiliary editor lost plan or changed scored actions");
                ProfileStore.Apply(record.ProfileSnapshot); LoadEditor();
                if (auxiliary.Rows.Count != 1 || Number(auxiliary.Rows[0], 3) != 1000) throw new Exception("Auxiliary profile reload lost rows");
                auxiliary.Rows[0].Cells["enabled"].Value = false;
                if (CreateRecord().ProfileSnapshot.AuxiliaryInputs[0].Enabled) throw new Exception("Auxiliary disable lost from snapshot");
                auxiliary.Rows.Clear();
                if (ReadEditor().SchemaVersion != 2 || ReadEditor().AuxiliaryInputs.Length != 0) throw new Exception("Auxiliary deletion downgraded schema or retained row");
            }
            finally { ProfileStore.Apply(before); LoadEditor(); settingsState.Text = previousState; }
        }
        internal static void VerifySavedProfileSwitch(string directory)
        {
            string original = ProfileStore.SourcePath;
            var modified = ProfileStore.Snapshot(); modified.Actions[0].Key = "Space";
            string saved = Path.Combine(directory, "profiles", modified.Id + ".json");
            try
            {
                ProfileStore.Apply(modified); ProfileStore.SaveCopy(saved);
                string expectedHash = ProfileStore.Hash; ProfileStore.Load(original);
                using (var form = new NoticeForm(directory, false)) form.SwitchProfile(saved);
                if (ProfileStore.Current.Actions[0].Key != "Space" || ProfileStore.Hash != expectedHash || ProfileStore.Sha256(File.ReadAllBytes(saved)) != expectedHash)
                    throw new Exception("Switching from bundled profile overwrote saved calibration");
            }
            finally { ProfileStore.Load(original); }
        }
        internal static void VerifyProfileChoices(string directory)
        {
            string original = ProfileStore.SourcePath;
            string app = Path.Combine(directory, "app", "run");
            string bundled = Path.Combine(directory, "tools", "experiment", "profiles", "boss.json");
            string saved = Path.Combine(app, "profiles", "boss.json");
            string variant = Path.Combine(app, "profiles", "variant.json");
            try
            {
                ProfileStore.SaveCopy(bundled); ProfileStore.SaveCopy(saved); ProfileStore.SaveCopy(variant); ProfileStore.Load(saved);
                using (var form = new NoticeForm(app, true))
                {
                    if (form.profiles.Items.Count != 2) throw new Exception("Bundled duplicate visible or named variant hidden");
                    File.WriteAllText(saved, "invalid"); ProfileStore.Load(variant); form.ReloadProfiles();
                    if (!form.profiles.Items.Cast<ProfileChoice>().Any(p => p.Path == bundled))
                        throw new Exception("Invalid saved copy hides bundled fallback");
                }
            }
            finally { ProfileStore.Load(original); }
        }
        internal static void VerifyAutoSave(string directory)
        {
            string original = ProfileStore.SourcePath;
            try
            {
                Directory.CreateDirectory(directory);
                var preparationFixture = ProfileStore.Snapshot(); preparationFixture.ManualPreparationRmbUntilMs = 0; preparationFixture.AllowManualMovement = false;
                preparationFixture.Preparation = new[] {
                    new PreparationPress { Id = "prepare-a", Label = "준비 방향 A", Key = "A", AtMs = 100, HoldMs = 600 },
                    new PreparationPress { Id = "prepare-first", Label = "준비 회피 1", Key = "RMB", AtMs = 200, HoldMs = 100 },
                    new PreparationPress { Id = "prepare-d", Label = "준비 방향 D", Key = "D", AtMs = 700, HoldMs = 400 },
                    new PreparationPress { Id = "prepare-second", Label = "준비 회피 2", Key = "RMB", AtMs = 800, HoldMs = 100 }
                };
                ProfileStore.Apply(preparationFixture);
                using (var form = new NoticeForm(directory, false))
                {
                    // Never show the form: no hotkeys, OBS connection or game input.
                    form.party.Text = "auto-save-party";
                    int last = form.timing.Rows.Count - 1;
                    form.timing.Rows[last].Cells["key"].Value = "RMB";
                    form.SelectTimingCell(last, 4);
                    form.preparation.Rows[0].Cells["at"].Value = 150;
                    form.preparation.Rows[0].Cells["hold"].Value = 500;
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    while (form.settingsTimer.Enabled && wait.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(10); }
                    string saved = ProfileStore.SourcePath;
                    if (!saved.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || ProfileStore.Parse(File.ReadAllText(saved)).Party != "auto-save-party")
                        throw new Exception("Settings timer did not persist edits");
                    var savedPreparation = ProfileStore.Parse(File.ReadAllText(saved)).Preparation;
                    if (savedPreparation[0].AtMs != 150 || savedPreparation[0].HoldMs != 500 || savedPreparation[0].Key != "A")
                        throw new Exception("Preparation timer did not persist timing edits");
                    string validHash = ProfileStore.Hash;
                    form.preparation.Rows[0].Cells["hold"].Value = 51;
                    bool rejectedPreparation = false;
                    try { form.PersistEditor(); } catch (ArgumentException) { rejectedPreparation = true; }
                    if (!rejectedPreparation || ProfileStore.Hash != validHash || ProfileStore.Parse(File.ReadAllText(saved)).Preparation[0].HoldMs != 500)
                        throw new Exception("Invalid preparation edit replaced valid saved settings");
                    form.preparation.Rows[0].Cells["hold"].Value = 500;
                    form.preparation.CurrentCell = form.preparation.Rows[0].Cells["at"];
                    if (!form.preparation.BeginEdit(true)) throw new Exception("Preparation numeric cell cannot be edited");
                    ((DataGridViewTextBoxEditingControl)form.preparation.EditingControl).Text = "200";
                    var editingRecord = form.CreateRecord();
                    if (editingRecord.ProfileSnapshot.Preparation[0].AtMs != 200 || form.preparation.IsCurrentCellInEditMode)
                        throw new Exception("F8 record did not finish the active preparation edit");
                    form.PersistEditor();
                    form.timing.CurrentCell = form.timing.Rows[0].Cells[2];
                    form.AdjustSelected(-50); form.PersistEditor();
                    var settings = form.ReadEditor();
                    if (ProfileStore.Parse(File.ReadAllText(saved)).Actions[0].Timing.EarlyMs != settings.Actions[0].Timing.EarlyMs)
                        throw new Exception("Timing adjustment did not persist");
                    if (ProfileStore.Parse(File.ReadAllText(saved)).Actions[last].Key != "RMB")
                        throw new Exception("Auto-save replaced follow-up RMB");
                    ProfileStore.Load(saved); form.LoadEditor(); form.RefreshSelection();
                    if (form.CreateRecord().ActionKeys[last] != "RMB" || form.CreateRecord().SelectedTimings[last] != "late") throw new Exception("Reload replaced follow-up key or selection");
                    if (Number(form.preparation.Rows[0], 2) != 200 || Number(form.preparation.Rows[0], 3) != 500)
                        throw new Exception("Reload replaced preparation timings");
                    string variant = Path.Combine(directory, "profiles", "party-variant.json"), sourceHash = ProfileStore.Sha256(File.ReadAllBytes(saved));
                    form.SaveProfileCopy(variant); form.party.Text = "variant-party"; form.PersistEditor();
                    if (ProfileStore.SourcePath != variant || ProfileStore.Sha256(File.ReadAllBytes(saved)) != sourceHash || ProfileStore.Parse(File.ReadAllText(variant)).Party != "variant-party")
                        throw new Exception("Save-as variant overwrote original profile");
                    var record = form.CreateRecord(new ObsObservation { State = "ON", AtUtc = DateTime.UtcNow }); record.Mode = ExperimentPlan.LiveMode; record.Status = "submitted-not-game-verified";
                    string pendingPath = form.pending.Begin(record); NoticeRecord.Save(pendingPath, record); form.SelectResult(pendingPath);
                    if (!form.resultHasPreparation || !form.preparationNote.Enabled || form.preparationNote.Text != "")
                        throw new Exception("Automatic preparation note is not optional and initially empty");
                    form.bits.Text = new String('1', record.ActionIds.Length); form.preparationNote.Text = "준비 중 피격"; form.SaveResult();
                    if (!form.obsState.Text.Contains("현재 녹화 OFF") || !form.resultTarget.Text.Contains("F8 녹화 ON"))
                        throw new Exception("Current and F8 recording displays are confused");
                    string accepted = form.resultPath;
                    if (form.recentTiming.Rows.Count != record.ActionIds.Length
                        || Convert.ToString(form.recentTiming.Rows[last].Cells[2].Value) != record.DelaysMs[last].ToString("0"))
                        throw new Exception("Recent archive differs from saved execution");
                    if (form.recentPreparation.Rows.Count != 4 || Number(form.recentPreparation.Rows[0], 2) != 200
                        || TrialResultStore.ReadPreparationNote(accepted) != "준비 중 피격" || !form.recentSummary.Text.Contains("준비 중 피격"))
                        throw new Exception("Recent archive lost preparation settings or optional note");
                    form.preparation.Rows[0].Cells["at"].Value = 150;
                    form.SelectTimingCell(last, 3); form.RefreshRecentTrial();
                    if (Convert.ToString(form.recentTiming.Rows[last].Cells[2].Value) != record.DelaysMs[last].ToString("0"))
                        throw new Exception("Current settings leaked into recent archive");
                    if (Number(form.recentPreparation.Rows[0], 2) != 200) throw new Exception("Current preparation leaked into recent archive");
                    var legacy = form.ReadEditor(); legacy.Preparation = null; ProfileStore.Apply(legacy); form.LoadEditor(); form.SelectResult(accepted);
                    if (form.preparation.Rows.Count != 0 || !form.resultHasPreparation || !form.preparationNote.Enabled)
                        throw new Exception("Result preparation visibility followed active profile instead of recorded snapshot");
                    form.SaveResult();
                    form.pending.Discard(); form.ClearResult();
                    if (!File.Exists(accepted) || form.bits.Enabled || form.saveBits.Enabled || form.resultPath != null || form.preparationNote.Enabled || form.resultHasPreparation)
                        throw new Exception("Result UI did not commit once or reset on next trial");
                    var legacyRecord = form.CreateRecord(); legacyRecord.Mode = ExperimentPlan.LiveMode; legacyRecord.Status = "submitted-not-game-verified";
                    pendingPath = form.pending.Begin(legacyRecord); NoticeRecord.Save(pendingPath, legacyRecord); form.SelectResult(pendingPath);
                    if (form.resultHasPreparation || form.preparationNote.Enabled || form.preparationNote.Text != "")
                        throw new Exception("Preparation note leaked into legacy result");
                    form.pending.Discard(); form.ClearResult();
                    pendingPath = form.pending.Begin(record); NoticeRecord.Save(pendingPath, record);
                    form.OnFormClosing(new FormClosingEventArgs(CloseReason.UserClosing, false));
                    if (File.Exists(pendingPath) || !File.Exists(accepted)) throw new Exception("Close did not discard pending only");
                }
            }
            finally { ProfileStore.Load(original); }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312)
            {
                if (message.WParam.ToInt32() == 9 && cancellation != null) cancellation.Cancel();
                if (message.WParam.ToInt32() == 8 && !running && !smoke && !editingResult) StartTrial();
            }
            base.WndProc(ref message);
        }
        private async void StartTrial()
        {
            DateTime f8Utc = DateTime.UtcNow;
            ObsObservation recordingAtF8 = FreshObservation();
            GameNoticeProbe probe = null;
            try
            {
                // Capture the foreground game at F8 before awaiting OBS or moving focus.
                var sink = new GameInputSink();
                PersistEditor(); var record = CreateRecord(recordingAtF8, f8Utc); NoticeRunner.Validate(record);
                probe = new GameNoticeProbe(sink.WindowHandle);
                pending.Discard(); ClearResult();
                resultTarget.Text = "이번 회차 F8 녹화 " + record.Recording + " · 이후 녹화 중지나 결과 저장 시점에 바뀌지 않습니다.";
                lastPath = pending.Begin(record);
                record.ProcessId = sink.ProcessId; record.ClientWidth = probe.ClientWidth; record.ClientHeight = probe.ClientHeight; record.DetectedImage = Path.ChangeExtension(lastPath, ".png");
                running = true; SetSettingsEnabled(false); cancellation = new CancellationTokenSource();
                lock (observationGate) { observations = new List<ObsObservation> { record.RecordingAtF8 }; }
                RefreshObs();
                var activeProbe = probe; string rawPath = lastPath;
                record = await Task.Run(() => NoticeRunner.Run(record, new StopwatchClock(), sink, activeProbe, () => cancellation.IsCancellationRequested,
                    r => {
                        var latest = FreshObservation();
                        lock (observationGate) { observations.Add(latest); r.ObsObservations = observations.ToArray(); }
                        NoticeRecord.Save(rawPath, r);
                    }, ReportProgress));
                state.Text = record.Status == "observed-no-input" ? "관찰 완료 · 게임 입력 없음" : record.Status == "submitted-not-game-verified" ? record.ActionIds.Length + "개 대응 제출 완료 · 결과를 입력하세요." : "중단: " + record.Error;
                if (record.Mode != "observe") SelectResult(rawPath); else pending.Discard();
            }
            catch (Exception e) { state.Text = "실행/기록 오류: " + e.Message; }
            finally
            {
                lock (observationGate) { observations = null; }
                if (probe != null) probe.Dispose(); running = false; SetSettingsEnabled(true);
                if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
                if (closing) Close();
            }
        }
        private void ReportProgress(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed && running) state.Text = text; })); } catch (InvalidOperationException) { }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { settingsTimer.Dispose(); obsTimer.Dispose(); obs.Dispose(); if (chosenFont != null) chosenFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
