using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VesperLab
{
    internal sealed class TrialCorrectionForm : Form
    {
        private readonly string path;
        private readonly ComboBox field = new ComboBox(), character = new ComboBox();
        private readonly TextBox results = new TextBox(), preparationNote = new TextBox();
        private readonly Label valueLabel = new Label(), error = new Label();
        private readonly Button save = new Button();
        private readonly string originalBits, originalCharacter, originalPreparationNote;
        internal static string LatestPath(string root)
        {
            string directory = Path.Combine(root, "notice-logs");
            if (!Directory.Exists(directory)) throw new ArgumentException("아직 결과를 보관한 회차가 없습니다.");
            // Result edits change write time, so choose archive creation order instead.
            string latest = Directory.GetFiles(directory, "trial-*.json")
                .Where(p => Path.GetFileNameWithoutExtension(p).IndexOf('.') < 0)
                .OrderByDescending(File.GetCreationTimeUtc).ThenByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (latest == null) throw new ArgumentException("아직 결과를 보관한 회차가 없습니다.");
            return latest;
        }
        internal TrialCorrectionForm(string rawPath)
        {
            path = rawPath;
            var record = TrialResultStore.ReadTrial(path);
            originalBits = TrialResultStore.ReadBits(path);
            if (String.IsNullOrEmpty(originalBits)) throw new ArgumentException("결과를 확정한 최근 회차만 정정할 수 있습니다.");
            originalCharacter = TrialResultStore.ReadStartCharacter(path);
            originalPreparationNote = TrialResultStore.ReadPreparationNote(path);
            Text = "최근 회차 정정"; Font = new Font("맑은 고딕", 9F);
            ClientSize = new Size(780, 350); MinimumSize = new Size(680, 390);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 26, 102, 42, 45, 38, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "마지막으로 보관한 회차만 정정합니다.", AutoSize = true }, 0, 0);
            DateTime at;
            string when = DateTime.TryParse(record.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)
                ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "시각 정보 없음";
            string boss = record.ProfileSnapshot == null ? record.BossId : record.ProfileSnapshot.BossLabel;
            string key = record.ActionKeys == null || record.ActionKeys.Length == 0 ? "미기록" : record.ActionKeys[0];
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = when + " · " + boss + " / " + record.Skill + "\n" +
                "진입 " + key + " · 시작 " + originalCharacter + " · 녹화 " + (record.Recording == "UNKNOWN" ? "OFF" : record.Recording) + "\n" +
                "입력 시각: " + String.Join(" / ", record.DelaysMs.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) + " ms\n" +
                "현재 결과: " + originalBits + "   (첫 자리 진입, 이후 1타부터 순서대로)" }, 0, 1);
            var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            choices.Controls.Add(new Label { Text = "수정할 항목", AutoSize = true, Margin = new Padding(0, 7, 12, 0) });
            field.DropDownStyle = ComboBoxStyle.DropDownList; field.Width = 230;
            field.Items.AddRange(new object[] { "성공/실패 결과", "시작 캐릭터" }); choices.Controls.Add(field); layout.Controls.Add(choices, 0, 2);
            if (record.ProfileSnapshot != null && record.ProfileSnapshot.Preparation != null && record.ProfileSnapshot.Preparation.Length > 0)
                field.Items.Add("준비 문제 메모");
            var values = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            valueLabel.AutoSize = true; valueLabel.Margin = new Padding(0, 7, 12, 0); values.Controls.Add(valueLabel);
            results.Width = 230; results.Text = originalBits; values.Controls.Add(results);
            character.Width = 230; character.MaxLength = 100; character.DropDownStyle = ComboBoxStyle.DropDown;
            string party = record.Party ?? (record.ProfileSnapshot == null ? "" : record.ProfileSnapshot.Party) ?? "";
            foreach (string name in party.Split(new[] { "→", ",", "/" }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Concat(new[] { originalCharacter }).Where(v => !String.IsNullOrWhiteSpace(v)).Distinct()) character.Items.Add(name);
            character.Text = originalCharacter; values.Controls.Add(character); layout.Controls.Add(values, 0, 3);
            preparationNote.Width = 370; preparationNote.MaxLength = 500; preparationNote.Text = originalPreparationNote; values.Controls.Add(preparationNote);
            error.Dock = DockStyle.Fill; error.ForeColor = Color.Firebrick; layout.Controls.Add(error, 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 32) };
            save.Text = "정정 저장"; save.AutoSize = true; save.MinimumSize = new Size(120, 32);
            save.Click += (s, e) => SaveCorrection(); buttons.Controls.Add(cancel); buttons.Controls.Add(save); layout.Controls.Add(buttons, 0, 5);
            AcceptButton = save; CancelButton = cancel;
            field.SelectedIndexChanged += (s, e) => RefreshField(record.ActionIds.Length);
            results.TextChanged += (s, e) => RefreshSave(); character.TextChanged += (s, e) => RefreshSave();
            preparationNote.TextChanged += (s, e) => RefreshSave();
            field.SelectedIndex = 0;
        }
        private void RefreshField(int count)
        {
            bool outcome = field.SelectedIndex == 0;
            valueLabel.Text = outcome ? "결과 " + count + "자리 (0 실패 / 1 성공)" : field.SelectedIndex == 1 ? "실제로 시작한 캐릭터" : "준비 피격·경직 등 (비우면 삭제)";
            results.Visible = outcome; character.Visible = field.SelectedIndex == 1; preparationNote.Visible = field.SelectedIndex == 2; error.Text = ""; RefreshSave();
        }
        private void RefreshSave()
        {
            save.Enabled = field.SelectedIndex == 0 ? results.Text != originalBits : field.SelectedIndex == 1
                ? character.Text.Trim() != originalCharacter : preparationNote.Text.Trim() != originalPreparationNote;
        }
        private void SaveCorrection()
        {
            try
            {
                if (field.SelectedIndex == 0) TrialResultStore.Save(path, results.Text.Trim());
                else if (field.SelectedIndex == 1) TrialResultStore.CorrectStartCharacter(path, character.Text.Trim());
                else TrialResultStore.Save(path, originalBits, preparationNote.Text);
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception exception) { error.Text = exception.Message; }
        }
        internal void SelectCharacterForSmoke() { field.SelectedIndex = 1; }
        internal static void Smoke(string output)
        {
            string root = Path.Combine(Path.GetTempPath(), "zzz-correction-ui-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "notice-logs"); Directory.CreateDirectory(directory);
            try
            {
                var trial = new NoticeRecord { Status = "submitted-not-game-verified", Mode = ExperimentPlan.LiveMode,
                    StartedUtc = DateTime.UtcNow.ToString("o"), StartCharacter = "아리아", Recording = "OFF" };
                string older = Path.Combine(directory, "trial-older.json"), latest = Path.Combine(directory, "trial-latest.json");
                trial.DelaysMs = ExperimentPlan.Resolve(trial.Candidates, "baseline");
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                File.WriteAllText(older, serializer.Serialize(trial)); trial.TrialId = Guid.NewGuid().ToString("N");
                File.WriteAllText(latest, serializer.Serialize(trial));
                string bits = new String('1', trial.ActionIds.Length);
                TrialResultStore.Save(older, bits); TrialResultStore.Save(latest, bits);
                File.SetCreationTimeUtc(older, DateTime.UtcNow.AddMinutes(-2)); File.SetCreationTimeUtc(latest, DateTime.UtcNow.AddMinutes(-1));
                File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(1));
                File.WriteAllText(Path.Combine(directory, "trial-ghost.json.start-character.txn.json"), "{}");
                if (LatestPath(root) != latest) throw new Exception("Result editing selected an older trial by write time");
                using (var form = new TrialCorrectionForm(latest))
                {
                    form.Show(); Application.DoEvents();
                    using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(output); }
                    form.results.Text = "invalid"; form.save.PerformClick();
                    if (form.DialogResult == DialogResult.OK || String.IsNullOrEmpty(form.error.Text)) throw new Exception("Invalid result accepted by correction UI");
                    form.field.SelectedIndex = 1; form.character.Text = "수나"; Application.DoEvents();
                    string second = Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + "-character.png");
                    using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(second); }
                    form.save.PerformClick();
                    if (form.DialogResult != DialogResult.OK || TrialResultStore.ReadStartCharacter(latest) != "수나" || TrialResultStore.ReadBits(latest) != bits)
                        throw new Exception("Starting-character UI did not preserve results");
                }
                if (LatestPath(root) != latest || TrialResultStore.ReadStartCharacter(older) != "아리아") throw new Exception("Correction changed latest selection or another trial");
                using (var form = new TrialCorrectionForm(latest))
                {
                    form.Show(); Application.DoEvents(); form.results.Text = "0" + bits.Substring(1); form.save.PerformClick();
                    if (form.DialogResult != DialogResult.OK || TrialResultStore.ReadBits(latest) != "0" + bits.Substring(1) || TrialResultStore.ReadStartCharacter(latest) != "수나")
                        throw new Exception("Results UI changed character or did not persist");
                }
                bool hasPreparation = trial.ProfileSnapshot != null && trial.ProfileSnapshot.Preparation != null && trial.ProfileSnapshot.Preparation.Length > 0;
                using (var form = new TrialCorrectionForm(latest))
                {
                    if (form.field.Items.Count != (hasPreparation ? 3 : 2)) throw new Exception("Preparation correction differs from trial snapshot");
                    if (hasPreparation)
                    {
                        string rawHash = ProfileStore.Sha256(File.ReadAllBytes(latest));
                        form.Show(); form.field.SelectedIndex = 2; form.preparationNote.Text = "둘째 준비 회피 뒤 피격"; Application.DoEvents();
                        string noteImage = Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + "-preparation.png");
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(noteImage); }
                        form.save.PerformClick();
                        if (form.DialogResult != DialogResult.OK || TrialResultStore.ReadPreparationNote(latest) != "둘째 준비 회피 뒤 피격"
                            || TrialResultStore.ReadBits(latest) != "0" + bits.Substring(1) || ProfileStore.Sha256(File.ReadAllBytes(latest)) != rawHash)
                            throw new Exception("Preparation correction changed results/raw or did not persist");
                    }
                }
                if (hasPreparation)
                {
                    using (var form = new TrialCorrectionForm(latest))
                    {
                        form.Show(); form.field.SelectedIndex = 2;
                        if (form.preparationNote.Text != "둘째 준비 회피 뒤 피격") throw new Exception("Preparation correction did not reload");
                        form.preparationNote.Text = ""; form.save.PerformClick();
                        if (form.DialogResult != DialogResult.OK || TrialResultStore.ReadPreparationNote(latest) != "") throw new Exception("Preparation correction did not clear");
                    }
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
