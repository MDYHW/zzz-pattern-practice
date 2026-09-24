using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VesperLab
{
    public sealed class ActionProfile
    {
        public string Id, Label, Key, Group;
        public string SelectedTiming = "baseline";
        public double HoldMs = 80;
        public double MinimumMs, MaximumMs;
        public TimingCandidate Timing;
    }
    public sealed class PreparationPress
    {
        public string Id, Label, Key;
        public double AtMs, HoldMs;
    }
    public sealed class AuxiliaryPress
    {
        public string Id, Label, Key;
        public double AtMs, HoldMs;
        public bool Enabled = true;
    }
    public sealed class StartAttackProfile
    {
        public string Key = "LMB";
        public double[] AtMs;
        public double HoldMs;
    }
    public sealed class DetectorProfile
    {
        public string Id { get; set; }
        public string Template { get; set; }
        public int ReferenceWidth { get; set; }
        public int ReferenceHeight { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int MinimumClientWidth { get; set; }
        public double Threshold { get; set; }
        public double PollMs { get; set; }
        public double AbsentMs { get; set; }
        public double ConfirmationMs { get; set; }
        public double TimeoutMs { get; set; }
    }
    public sealed class ExperimentProfile
    {
        public int SchemaVersion;
        public string Id, BossId, BossLabel, Skill, Conditions;
        public string Party, StartCharacter;
        public double MinimumSpacingMs;
        public double ManualPreparationRmbUntilMs;
        public PreparationPress[] Preparation;
        public AuxiliaryPress[] AuxiliaryInputs;
        public StartAttackProfile StartAttack;
        public bool AllowManualMovement;
        public DetectorProfile Detector;
        public ActionProfile[] Actions;
    }
    public static class ProfileStore
    {
        public static ExperimentProfile Current { get; private set; }
        public static string SourcePath { get; private set; }
        public static string Hash { get; private set; }
        public static string SourceHash { get; private set; }
        public static string TemplatePath { get; private set; }
        public static string TemplateHash { get; private set; }
        private static byte[] templateBytes;
        public static Stream OpenTemplate() { return new MemoryStream(templateBytes, false); }
        public static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static void Require(bool value, string message) { if (!value) throw new ArgumentException("프로필 오류: " + message); }
        public static void Validate(ExperimentProfile p)
        {
            Require(p != null && (p.SchemaVersion == 1 || p.SchemaVersion == 2), "지원하는 SchemaVersion은 1 또는 2입니다.");
            Require(!String.IsNullOrWhiteSpace(p.Id) && Regex.IsMatch(p.Id, @"^[a-z0-9][a-z0-9-]{0,79}$"), "명시적인 Id가 필요합니다.");
            Require(!String.IsNullOrWhiteSpace(p.BossId) && !String.IsNullOrWhiteSpace(p.BossLabel) && !String.IsNullOrWhiteSpace(p.Skill) && !String.IsNullOrWhiteSpace(p.Conditions), "보스·스킬·실험 조건이 필요합니다.");
            Require(Finite(p.ManualPreparationRmbUntilMs) && p.ManualPreparationRmbUntilMs >= 0, "수동 준비 RMB 종료 시각은 유한한 비음수여야 합니다.");
            var preparation = p.Preparation ?? new PreparationPress[0];
            Require(preparation.Length <= 32 && preparation.All(a => a != null), "자동 준비 입력은 최대 32개이며 빈 항목은 허용하지 않습니다.");
            Require(preparation.Length == 0 || p.ManualPreparationRmbUntilMs == 0, "자동 준비와 수동 준비 RMB 허용을 함께 사용할 수 없습니다.");
            Require(!p.AllowManualMovement || preparation.All(a => a.Key == "RMB"), "수동 이동 허용과 자동 준비 방향 입력을 함께 사용할 수 없습니다.");
            Require(preparation.Select(a => a.Id).Distinct().Count() == preparation.Length, "준비 Id는 중복할 수 없습니다.");
            foreach (var a in preparation)
            {
                Require(!String.IsNullOrWhiteSpace(a.Id) && !String.IsNullOrWhiteSpace(a.Label) && (a.Key == "A" || a.Key == "D" || a.Key == "RMB"), "준비 Id·이름·A/D/RMB 키를 확인하세요.");
                Require(Finite(a.AtMs) && Finite(a.HoldMs) && a.AtMs >= 100 && a.HoldMs >= 50 && a.AtMs + a.HoldMs <= 120000 && a.AtMs % 50 == 0 && a.HoldMs % 50 == 0, "준비 시각·유지 시간은 유한한 50ms 단위이며 시작 100ms 이상·유지 50ms 이상이어야 합니다.");
            }
            for (int i = 0; i < preparation.Length; i++)
                for (int j = i + 1; j < preparation.Length; j++)
                {
                    var a = preparation[i]; var b = preparation[j];
                    bool conflicts = a.Key == b.Key || (a.Key != "RMB" && b.Key != "RMB");
                    Require(!conflicts || a.AtMs + a.HoldMs <= b.AtMs || b.AtMs + b.HoldMs <= a.AtMs, "같은 준비 키 또는 반대 방향의 유지 시간이 겹칩니다.");
                }
            var auxiliary = p.AuxiliaryInputs ?? new AuxiliaryPress[0];
            Require(auxiliary.Length <= 32 && auxiliary.All(a => a != null), "비채점 보조 입력은 최대 32개이며 빈 항목은 허용하지 않습니다.");
            Require(auxiliary.Length == 0 || p.SchemaVersion == 2, "비채점 보조 입력이 있는 프로필은 SchemaVersion 2여야 합니다.");
            Require(auxiliary.Select(a => a.Id).Distinct().Count() == auxiliary.Length, "비채점 보조 입력 Id는 중복할 수 없습니다.");
            foreach (var a in auxiliary)
            {
                Require(!String.IsNullOrWhiteSpace(a.Id) && !String.IsNullOrWhiteSpace(a.Label)
                    && (a.Key == "LMB" || a.Key == "RMB" || a.Key == "Space"), "비채점 보조 입력 Id·이름·LMB/RMB/Space 키를 확인하세요.");
                Require(Finite(a.AtMs) && Finite(a.HoldMs) && a.AtMs >= 100 && a.HoldMs >= 50
                    && a.AtMs + a.HoldMs <= 120000 && a.AtMs % 50 == 0 && a.HoldMs % 50 == 0,
                    "비채점 보조 입력 시각·유지 시간은 유한한 50ms 단위이며 시작 100ms 이상·유지 50ms 이상이어야 합니다.");
            }
            Require(!auxiliary.Any(a => a.Enabled) || p.ManualPreparationRmbUntilMs == 0,
                "활성 비채점 보조 입력과 수동 준비 RMB 허용을 함께 사용할 수 없습니다.");
            // Legacy range/spacing fields are retained for reading old profiles, not exploration limits.
            var d = p.Detector;
            Require(d != null && !String.IsNullOrWhiteSpace(d.Id) && !String.IsNullOrWhiteSpace(d.Template), "문구 감지 설정과 템플릿이 필요합니다.");
            Require(d.ReferenceWidth >= 320 && d.ReferenceHeight >= 180 && d.ReferenceWidth <= 8192 && d.ReferenceHeight <= 8192 && d.MinimumClientWidth >= 320, "기준 화면 크기를 확인하세요.");
            Require(d.X >= 0 && d.Y >= 0 && d.Width >= 4 && d.Width <= 2048 && d.Width % 4 == 0 && d.Height > 0 && d.Height <= 512 && d.X + d.Width <= d.ReferenceWidth && d.Y + d.Height <= d.ReferenceHeight, "문구 영역이 기준 화면 밖이거나 RGB24 크기가 잘못됐습니다.");
            Require(Finite(d.Threshold) && d.Threshold > 0 && d.Threshold <= 1 && Finite(d.PollMs) && d.PollMs >= 1 && d.PollMs <= 50 && Finite(d.AbsentMs) && d.AbsentMs >= 50 && d.AbsentMs <= 2000 && Finite(d.ConfirmationMs) && d.ConfirmationMs >= 1 && d.ConfirmationMs <= 1000 && Finite(d.TimeoutMs) && d.TimeoutMs >= 1000 && d.TimeoutMs <= 120000, "감지 임계값과 대기 시각을 확인하세요.");
            if (p.StartAttack != null)
            {
                var start = p.StartAttack;
                Require(start.Key == "LMB" || start.Key == "E", "시작 입력 키는 LMB 또는 E여야 합니다.");
                Require(start.AtMs != null && start.AtMs.Length == (start.Key == "E" ? 1 : 5), "시작 입력은 LMB 다섯 번 또는 E 한 번이어야 합니다.");
                Require(Finite(start.HoldMs) && start.HoldMs >= 50 && start.HoldMs <= 2000 && start.HoldMs % 50 == 0, "시작 입력 유지 시간은 50~2000ms 내 50ms 단위여야 합니다.");
                for (int i = 0; i < start.AtMs.Length; i++)
                    Require(Finite(start.AtMs[i]) && start.AtMs[i] >= 100 && start.AtMs[i] % 50 == 0
                        && start.AtMs[i] + start.HoldMs < d.TimeoutMs
                        && (i == 0 || start.AtMs[i] >= start.AtMs[i - 1] + start.HoldMs),
                        "시작 입력 시각은 100ms 이상·50ms 단위이며, 순서대로 유지가 겹치지 않고 감지 대기 종료 전에 끝나야 합니다.");
            }
            Require(p.Actions != null && p.Actions.Length >= 1 && p.Actions.Length <= 12 && p.Actions.All(a => a != null), "대응 수는 1~12개입니다.");
            Require(p.Actions.Select(a => a.Id).Distinct().Count() == p.Actions.Length, "대응 Id는 중복할 수 없습니다.");
            foreach (var a in p.Actions)
            {
                Require(!String.IsNullOrWhiteSpace(a.Id) && !String.IsNullOrWhiteSpace(a.Label) && (a.Key == "RMB" || a.Key == "Space") && (a.Group == "A" || a.Group == "B"), "대응 Id·이름·RMB/Space·A/B 그룹을 확인하세요.");

                Require(ExperimentPlan.TimingIds.Contains(a.SelectedTiming), "실행 시각은 early/baseline/late 중 하나여야 합니다.");
                Require(Finite(a.HoldMs) && (a.HoldMs == 80 || (a.HoldMs >= 50 && a.HoldMs <= 2000 && a.HoldMs % 50 == 0)), "대응 유지 시간은 50~2000ms 내 50ms 단위 또는 기존값 80ms여야 합니다.");
                ValidateTiming(a, a.Timing);
            }
            if (auxiliary.Any(a => a.Enabled))
            {
                var selected = p.Actions.Select(a => new[] { a.Timing.EarlyMs, a.Timing.BaselineMs, a.Timing.LateMs }[ExperimentPlan.TimingColumn(a.SelectedTiming)]).ToArray();
                ExperimentPlan.ValidateAuxiliarySchedule(p, selected);
            }
        }
        public static void ValidateTiming(ActionProfile action, TimingCandidate row)
        {
            Require(row != null, "대응 시각이 비어 있습니다.");
            Require(new[] { row.EarlyMs, row.BaselineMs, row.LateMs }.All(v => Finite(v) && v >= 100 && v <= 120000 && v % 50 == 0), action.Label + " 시각은 100~120000ms 내 50ms 단위여야 합니다.");
            Require(row.EarlyMs <= row.BaselineMs && row.BaselineMs <= row.LateMs, "이른 값 ≤ 기준값 ≤ 늦은 값이어야 합니다.");
        }
        public static ExperimentProfile Parse(string json)
        {
            var p = new JavaScriptSerializer().Deserialize<ExperimentProfile>(json); Validate(p); return p;
        }
        public static void Load(string path)
        {
            string full = Path.GetFullPath(path); byte[] bytes = File.ReadAllBytes(full);
            var p = Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            SetCurrent(p, full, Sha256(bytes), Sha256(bytes));
        }
        private static void SetCurrent(ExperimentProfile p, string full, string hash, string sourceHash)
        {
            Validate(p);
            string template = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full), p.Detector.Template));
            byte[] imageBytes = File.ReadAllBytes(template);
            using (var stream = new MemoryStream(imageBytes, false))
            using (var image = new System.Drawing.Bitmap(stream))
                Require(image.Width == p.Detector.Width && image.Height == p.Detector.Height, "템플릿 크기와 문구 영역 크기가 다릅니다.");
            var previous = Current; string previousPath = SourcePath, previousHash = Hash, previousSourceHash = SourceHash, previousTemplate = TemplatePath, previousTemplateHash = TemplateHash;
            byte[] previousBytes = templateBytes;
            Current = p; SourcePath = full; Hash = hash; SourceHash = sourceHash; TemplatePath = template; TemplateHash = Sha256(imageBytes); templateBytes = imageBytes;
            try { new NoticeMatcher(); }
            catch
            {
                Current = previous; SourcePath = previousPath; Hash = previousHash; SourceHash = previousSourceHash;
                TemplatePath = previousTemplate; TemplateHash = previousTemplateHash; templateBytes = previousBytes;
                throw;
            }
        }
        public static void Apply(ExperimentProfile p)
        {
            string json = new JavaScriptSerializer().Serialize(p);
            SetCurrent(Parse(json), SourcePath, Sha256(System.Text.Encoding.UTF8.GetBytes(json)), SourceHash);
        }
        public static void SaveCopy(string path)
        {
            string full = Path.GetFullPath(path), directory = Path.GetDirectoryName(full);
            Directory.CreateDirectory(directory);
            var copy = Snapshot();
            copy.Detector.Template = "template-" + TemplateHash + ".png";
            string imagePath = Path.Combine(directory, copy.Detector.Template);
            if (!File.Exists(imagePath)) File.WriteAllBytes(imagePath, templateBytes);
            else Require(Sha256(File.ReadAllBytes(imagePath)) == TemplateHash, "저장할 템플릿 파일의 내용이 달라졌습니다.");
            string temp = full + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(copy));
            if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            Load(full);
        }
        public static ExperimentProfile Snapshot()
        {
            var serializer = new JavaScriptSerializer();
            return serializer.Deserialize<ExperimentProfile>(serializer.Serialize(Current));
        }
    }
}
