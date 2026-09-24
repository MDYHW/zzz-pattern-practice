using System;
using System.Linq;

namespace VesperLab
{
    public sealed class TimingCandidate { public double EarlyMs, BaselineMs, LateMs; }
    public static class ExperimentPlan
    {
        private sealed class ControlHold
        {
            public string Label;
            public double AtMs, HoldMs;
        }
        public const string Version = "3.11.0", LiveMode = "scheduled-input", PerActionPattern = "per-action";
        public const double HoldMs = 80;
        public static readonly string[] TimingIds = { "early", "baseline", "late" };
        public static string Skill { get { return ProfileStore.Current.Skill; } }
        public static string Detector { get { return ProfileStore.Current.Detector.Id; } }
        public static string Conditions { get { return ProfileStore.Current.Conditions; } }
        public static int Count { get { return ProfileStore.Current.Actions.Length; } }
        public static readonly string[] PatternIds = { "baseline", "entry-early", "entry-late", "a-early", "b-early", "a-late", "b-late", "a-late-b-early", "a-early-b-late" };
        public static readonly string[] PatternLabels = { "전체 기준", "진입만 이르게 / 이후 기준", "진입만 늦게 / 이후 기준", "A 이르게 / B 기준", "A 기준 / B 이르게", "A 늦게 / B 기준", "A 기준 / B 늦게", "A 늦게 / B 이르게", "A 이르게 / B 늦게" };
        public static TimingCandidate[] Defaults()
        {
            return ProfileStore.Current.Actions.Select(a => new TimingCandidate { EarlyMs = a.Timing.EarlyMs, BaselineMs = a.Timing.BaselineMs, LateMs = a.Timing.LateMs }).ToArray();
        }
        public static int TimingColumn(string selectedTiming)
        {
            int column = Array.IndexOf(TimingIds, selectedTiming);
            if (column < 0) throw new ArgumentException("실행 시각은 early/baseline/late 중 하나여야 합니다.");
            return column;
        }
        public static string[] SelectedTimings(string pattern)
        {
            return Enumerable.Range(0, Count).Select(i => TimingIds[Column(pattern, i)]).ToArray();
        }
        public static int Column(string pattern, int attack)
        {
            return Column(pattern, attack, ProfileStore.Current.Actions[attack].Group);
        }
        public static int Column(string pattern, int attack, string group)
        {
            if (pattern == PerActionPattern) return TimingColumn(ProfileStore.Current.Actions[attack].SelectedTiming);
            bool a = group == "A";
            switch (pattern)
            {
                case "baseline": return 1;
                case "entry-early": return attack == 0 ? 0 : 1;
                case "entry-late": return attack == 0 ? 2 : 1;
                case "a-early": return a ? 0 : 1;
                case "b-early": return a ? 1 : 0;
                case "a-late": return a ? 2 : 1;
                case "b-late": return a ? 1 : 2;
                case "a-late-b-early": return a ? 2 : 0;
                case "a-early-b-late": return a ? 0 : 2;
                default: throw new ArgumentException("알 수 없는 실험 조합입니다.");
            }
        }
        public static void ValidateCandidates(TimingCandidate[] candidates)
        {
            if (candidates == null || candidates.Length != Count) throw new ArgumentException(Skill + " 대응 수와 설정이 일치해야 합니다.");
            for (int i = 0; i < Count; i++) ProfileStore.ValidateTiming(ProfileStore.Current.Actions[i], candidates[i]);
        }
        public static TimingCandidate[] LoadCandidates(string path)
        {
            var rows = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<TimingCandidate[]>(System.IO.File.ReadAllText(path));
            ValidateCandidates(rows); return rows;
        }
        public static double[] Resolve(TimingCandidate[] candidates, string pattern)
        {
            ValidateCandidates(candidates);
            var values = new double[Count];
            for (int i = 0; i < Count; i++)
            {
                var row = candidates[i]; values[i] = new[] { row.EarlyMs, row.BaselineMs, row.LateMs }[Column(pattern, i)];
            }
            ValidateAuxiliarySchedule(ProfileStore.Current, values);
            return values;
        }
        public static void ValidateAuxiliarySchedule(ExperimentProfile profile, double[] values)
        {
            if (profile == null || profile.Actions == null || values == null || values.Length != profile.Actions.Length)
                throw new ArgumentException("대응 수와 실행 시각이 일치해야 합니다.");
            for (int i = 1; i < values.Length; i++)
                if (values[i] - values[i - 1] < profile.Actions[i - 1].HoldMs)
                    throw new ArgumentException("이번 조합의 입력 순서 또는 앞 대응의 키 유지 시간(" + profile.Actions[i - 1].HoldMs + "ms)이 겹칩니다. 선택한 시각을 조정하세요.");
            if (profile.ManualPreparationRmbUntilMs > 0 && values[0] <= profile.ManualPreparationRmbUntilMs)
                throw new ArgumentException("이번 조합의 첫 자동 입력은 수동 준비 RMB 종료 시각 뒤여야 합니다.");
            foreach (var press in profile.Preparation ?? new PreparationPress[0])
            {
                double end = press.AtMs + press.HoldMs;
                if (press.AtMs >= values[0] || (press.Key == "RMB" && end >= values[0]) || (press.Key != "RMB" && end >= (values.Length > 1 ? values[1] : values[0] + profile.Actions[0].HoldMs)))
                    throw new ArgumentException("준비 RMB는 진입 전에 해제하고, 준비 방향은 진입 전에 시작하여 다음 대응 전에 해제해야 합니다.");
            }
            var controls = new System.Collections.Generic.List<ControlHold>();
            foreach (var press in profile.Preparation ?? new PreparationPress[0])
                if (press.Key == "RMB") controls.Add(new ControlHold { Label = press.Label, AtMs = press.AtMs, HoldMs = press.HoldMs });
            foreach (var press in profile.AuxiliaryInputs ?? new AuxiliaryPress[0])
                if (press.Enabled) controls.Add(new ControlHold { Label = press.Label, AtMs = press.AtMs, HoldMs = press.HoldMs });
            for (int i = 0; i < profile.Actions.Length; i++)
                controls.Add(new ControlHold { Label = profile.Actions[i].Label, AtMs = values[i], HoldMs = profile.Actions[i].HoldMs });
            for (int i = 0; i < controls.Count; i++)
                for (int j = i + 1; j < controls.Count; j++)
                {
                    var a = controls[i]; var b = controls[j];
                    if (a.AtMs < b.AtMs + b.HoldMs && b.AtMs < a.AtMs + a.HoldMs)
                        throw new ArgumentException("자동 control 입력 유지 시간이 겹칩니다: " + a.Label + " / " + b.Label + ".");
                }
        }
    }
}
