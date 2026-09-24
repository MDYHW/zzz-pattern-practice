using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VesperLab
{
    internal static class NoticeTests
    {
        private sealed class Clock : ITrialClock
        {
            public double Now;
            public long Timestamp { get { return (long)(Now * 1000); } }
            public long Frequency { get { return 1000000; } }
            public double ElapsedMs(long origin) { return Now - origin / 1000.0; }
            public void WaitUntil(long origin, double due, Func<bool> cancel)
            {
                double target = origin / 1000.0 + due;
                while (!cancel() && Now < target) Now = Math.Min(target, Now + 1);
            }
        }
        private sealed class Sink : IInputSink
        {
            public List<string> Calls = new List<string>();
            public Func<string, bool> HeldExcept = key => false;
            public Func<string, bool> DirectionExcept = key => false;
            public Func<bool> LmbHeld = () => false;
            public Func<bool> EHeld = () => false;
            public Func<bool> Foreground = () => true, Held = () => false;
            public bool FailDown, FailUp, ThrowDown, ThrowUp;
            public string FailKey;
            public Action<string, bool> OnSend = (key, down) => {};
            public string ProcessName { get { return "fake"; } }
            public int ProcessId { get { return 0; } }
            public string WindowLabel { get { return "fake"; } }
            public bool IsTargetForeground() { return Foreground(); }
            public bool AnyControlHeld(string ownedKey = null, bool includeLmb = false, bool includeE = false)
            {
                return Held() || HeldExcept(ownedKey) || (includeLmb && ownedKey != "LMB" && LmbHeld())
                    || (includeE && ownedKey != "E" && EHeld());
            }
            public bool AnyDirectionHeld(string ownedKey = null) { return DirectionExcept(ownedKey); }
            public int Send(string key, bool down, out int error)
            {
                Calls.Add(key + (down ? "-down" : "-up"));
                OnSend(key, down);
                if ((FailKey == null || FailKey == key) && ((down && ThrowDown) || (!down && ThrowUp))) throw new IOException("submission exception");
                error = (FailKey == null || FailKey == key) && ((down && FailDown) || (!down && FailUp)) ? 5 : 0; return error == 0 ? 1 : 0;
            }
        }
        private sealed class Probe : INoticeProbe
        {
            public Func<double, bool> Visible = t => t >= 200;
            public double CaptureCost = 2;
            public Action Geometry = () => {};
            public NoticeSample Capture(ITrialClock clock, long origin)
            {
                var c = (Clock)clock; double begin = c.ElapsedMs(origin); c.Now += CaptureCost;
                bool present = Visible(begin);
                return new NoticeSample { CaptureBeginMs = begin, CaptureEndMs = c.ElapsedMs(origin), AnalysisEndMs = c.ElapsedMs(origin), Present = present, Score = present ? .9 : .1 };
            }
            public void CheckGeometry() { Geometry(); }
            public void SaveDetectedImage(string path) { }
            public void Dispose() { }
        }
        private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static NoticeRecord Settings(string pattern = "baseline", string mode = ExperimentPlan.LiveMode)
        {
            return new NoticeRecord { Mode = mode, Pattern = pattern, Recording = "ON", TimeoutMs = 1000,
                Candidates = ExperimentPlan.Defaults(), DelaysMs = ExperimentPlan.Resolve(ExperimentPlan.Defaults(), pattern) };
        }
        private static NoticeRecord Trial(NoticeRecord r, Clock clock = null, Sink sink = null, Probe probe = null,
            Func<bool> cancel = null, Action<NoticeRecord> save = null)
        {
            return NoticeRunner.Run(r, clock ?? new Clock(), sink ?? new Sink(), probe ?? new Probe(), cancel ?? (() => false), save ?? (x => {}), null);
        }
        public static int Run(string output, bool profileOnly = false)
        {
            string original = ProfileStore.SourcePath, selectedId = ProfileStore.Current.Id, selectedHash = ProfileStore.Hash;
            string fixture = Path.GetTempFileName();
            try
            {
                // Exercise the selected profile first. Core failure scenarios below use a stable
                // synthetic fixture so future action counts, keys and groups do not invalidate tests.
                foreach (string pattern in ExperimentPlan.PatternIds)
                {
                    var r = new NoticeRecord { Mode = ExperimentPlan.LiveMode, Pattern = pattern, Recording = "ON", DelaysMs = ExperimentPlan.Resolve(ExperimentPlan.Defaults(), pattern) };
                    var sink = new Sink(); double appears = ProfileStore.Current.Detector.AbsentMs + 100;
                    if (ProfileStore.Current.StartAttack != null) appears = Math.Max(appears, ProfileStore.Current.StartAttack.AtMs.Last() + ProfileStore.Current.StartAttack.HoldMs + 100);
                    if (ProfileStore.Current.StartInputs != null && ProfileStore.Current.StartInputs.Length > 0)
                        appears = Math.Max(appears, ProfileStore.Current.StartInputs.Max(a => a.AtMs + a.HoldMs) + 100);
                    Trial(r, sink:sink, probe:new Probe { Visible = t => t >= appears });
                    Expect(r.Status == "submitted-not-game-verified", "selected profile schedule cannot complete: " + r.Error);
                    Expect(r.Inputs.Where(e => e.Action == "down").Select(e => e.Key).SequenceEqual(ProfileStore.Current.Actions.Select(a => a.Key)), "selected profile key sequence differs");
                    Expect(r.ProfileHash == selectedHash && r.ProfileId == selectedId && r.GameOutcome == "미확인", "selected profile provenance/outcome changed");
                }
                using (var form = new NoticeForm(Path.GetTempPath(), true)) { form.VerifySmokeSettings(); }
                if (profileOnly)
                {
                    File.WriteAllText(output, new JavaScriptSerializer().Serialize(new {
                        profileId = selectedId, profileHash = selectedHash,
                        selected_profile_combinations_passed = ExperimentPlan.PatternIds.Length,
                        common_core_tests_run = false, native_input_calls = 0,
                        passed = new[] { "selected profile schedules and editor snapshots" }
                    }));
                    return 0;
                }
                var p = ProfileStore.Snapshot(); p.Id = "synthetic-core-tests"; p.Detector.Template = ProfileStore.TemplatePath;
                p.ManualPreparationRmbUntilMs = 0;
                p.Preparation = null;
                p.StartAttack = null;
                p.StartInputs = null;
                p.AuxiliaryInputs = null;
                p.AllowManualMovement = false;
                p.MinimumSpacingMs = 250; p.Detector.PollMs = 8; p.Detector.AbsentMs = 100; p.Detector.ConfirmationMs = 40; p.Detector.TimeoutMs = 60000;
                p.Actions = Enumerable.Range(0, 6).Select(i => new ActionProfile { Id="test-"+i, Label="test "+i, Key=i==0 ? "RMB" : "Space", Group=i%2==0 ? "A" : "B", MinimumMs=100, MaximumMs=20000,
                    Timing=new TimingCandidate { EarlyMs=1900+i*1500, BaselineMs=2000+i*1500, LateMs=2100+i*1500 } }).ToArray();
                File.WriteAllText(fixture, new JavaScriptSerializer().Serialize(p)); ProfileStore.Load(fixture);
                int code = RunCore(output);
                var serializer = new JavaScriptSerializer();
                var report = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(output));
                report["profileId"] = selectedId; report["profileHash"] = selectedHash; report["selected_profile_combinations_passed"] = ExperimentPlan.PatternIds.Length;
                File.WriteAllText(output, serializer.Serialize(report)); return code;
            }
            finally { File.Delete(fixture); ProfileStore.Load(original); }
        }
        private static int RunCore(string output)
        {
            var passed = new List<string>();
            Action<string, Action> test = (name, action) => { Console.WriteLine(name); action(); passed.Add(name); };
            ObsRecordingTests.Run(test);
            TrialResultsTests.Run(test);
            ManualPreparationTests(test);
            AutomaticPreparationTests(test);
            AuxiliaryInputTests(test);
            ActionHoldTests(test);
            StartAttackTests(test);
            StartInputTests(test);
            ManualMovementTests(test);
            test("edited profile saves party, entry key, count and a portable template", () => {
                string source = ProfileStore.SourcePath, directory = Path.Combine(Path.GetTempPath(), "profile-edit-" + Guid.NewGuid().ToString("N"));
                try
                {
                    NoticeForm.VerifySavedProfileSwitch(Path.Combine(directory, "switch"));
                    NoticeForm.VerifyAutoSave(Path.Combine(directory, "auto-save"));
                    NoticeForm.VerifyProfileChoices(Path.Combine(directory, "choices"));
                    var p = ProfileStore.Snapshot(); p.Party = "A → B → C"; p.StartCharacter = "B"; p.Actions = p.Actions.Take(4).ToArray(); p.Actions[0].Key = "Space";
                    ProfileStore.Apply(p); string hash = ProfileStore.TemplateHash;
                    ProfileStore.SaveCopy(Path.Combine(directory, "profile.json"));
                    Expect(ProfileStore.Current.Party == p.Party && ProfileStore.Current.StartCharacter == "B" && ExperimentPlan.Count == 4 && ProfileStore.Current.Actions[0].Key == "Space", "edited settings not persisted");
                    Expect(ProfileStore.TemplateHash == hash && File.Exists(ProfileStore.TemplatePath) && Path.GetDirectoryName(ProfileStore.TemplatePath) == directory, "save-as lost template");
                    foreach (string preset in new[] { "entry-early", "entry-late" })
                    {
                        var values = ExperimentPlan.Resolve(ExperimentPlan.Defaults(), preset);
                        Expect(values.Skip(1).SequenceEqual(ExperimentPlan.Defaults().Skip(1).Select(t => t.BaselineMs)), "entry-only preset changed later actions");
                    }
                    string before = ProfileStore.Hash; p = ProfileStore.Snapshot(); p.Detector.Template = "missing.png";
                    bool rejected = false; try { ProfileStore.Apply(p); } catch (IOException) { rejected = true; }
                    Expect(rejected && ProfileStore.Hash == before && ProfileStore.TemplateHash == hash, "failed edit changed active profile");
                }
                finally { ProfileStore.Load(source); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
            test("legacy profiles default each action to baseline and reject invalid selections", () => {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(ProfileStore.Snapshot());
                string legacy = json.Replace(",\"SelectedTiming\":\"baseline\"", "").Replace("\"SelectedTiming\":\"baseline\",", "");
                Expect(!legacy.Contains("SelectedTiming"), "legacy fixture still contains selections");
                var loaded = ProfileStore.Parse(legacy);
                Expect(loaded.Actions.All(a => a.SelectedTiming == "baseline"), "missing selection is not baseline");
                foreach (string invalid in new[] { "", "middle", "Early", null })
                {
                    loaded.Actions[0].SelectedTiming = invalid;
                    bool rejected = false; try { ProfileStore.Validate(loaded); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "invalid selection accepted");
                }
                var oldRecord = serializer.Deserialize<NoticeRecord>("{\"Pattern\":\"a-late\"}");
                Expect(oldRecord.SelectedTimings == null, "old record inherited current selections");
            });
            test("per-action choices drive independent timings and keys and survive log roundtrip", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = ProfileStore.Snapshot();
                    string[] selected = { "baseline", "late", "baseline", "early", "late", "early" };
                    string[] keys = { "Space", "RMB", "Space", "RMB", "RMB", "Space" };
                    for (int i = 0; i < p.Actions.Length; i++) { p.Actions[i].SelectedTiming = selected[i]; p.Actions[i].Key = keys[i]; }
                    ProfileStore.Apply(p);
                    var sink = new Sink(); var r = Trial(Settings(ExperimentPlan.PerActionPattern), sink: sink);
                    double[] expected = { 2000, 3600, 5000, 6400, 8100, 9400 };
                    Expect(r.Status == "submitted-not-game-verified" && r.DelaysMs.SequenceEqual(expected), "independent timings not executed");
                    Expect(sink.Calls.SequenceEqual(keys.SelectMany(k => new[] { k + "-down", k + "-up" })), "per-action keys not executed");
                    double origin = r.Samples[r.FirstMatchIndex].CaptureEndMs;
                    Expect(r.Inputs.Where(i => i.Action == "down").Select(i => i.BeginMs).SequenceEqual(expected.Select(v => origin + v)), "per-action deadlines drifted");
                    var serializer = new JavaScriptSerializer(); var copy = serializer.Deserialize<NoticeRecord>(serializer.Serialize(r));
                    Expect(copy.SelectedTimings.SequenceEqual(selected) && copy.ProfileSnapshot.Actions.Select(a => a.SelectedTiming).SequenceEqual(selected), "selected cells lost in saved log");
                    Expect(copy.ActionKeys.SequenceEqual(keys) && copy.DelaysMs.SequenceEqual(expected), "actual settings lost in saved log");
                    p.Actions[0].SelectedTiming = "early"; ProfileStore.Apply(p);
                    Expect(copy.SelectedTimings[0] == "baseline" && copy.ProfileSnapshot.Actions[0].SelectedTiming == "baseline", "saved selection changed with active profile");
                    var stale = Settings(ExperimentPlan.PerActionPattern); stale.SelectedTimings = selected;
                    var rejectedSink = new Sink(); Trial(stale, sink: rejectedSink);
                    Expect(stale.Status == "failed" && rejectedSink.Calls.Count == 0 && stale.Samples.Count == 0, "mismatched cell choices armed");
                    p.Actions[1].Timing = new TimingCandidate { EarlyMs = 1900, BaselineMs = 1950, LateMs = 1950 }; ProfileStore.Apply(p);
                    bool collision = false;
                    try { ExperimentPlan.Resolve(ExperimentPlan.Defaults(), ExperimentPlan.PerActionPattern); } catch (ArgumentException) { collision = true; }
                    Expect(collision, "per-action overlapping key holds accepted");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("exploration ignores legacy bounds and cross-candidate spacing but rejects selected collisions", () => {
                var original = ProfileStore.Snapshot();
                try {
                    var p = ProfileStore.Snapshot();
                    p.MinimumSpacingMs = 20000;
                    p.Actions = p.Actions.Take(2).ToArray();
                    p.Actions[0].MinimumMs = 3000; p.Actions[0].MaximumMs = 4000;
                    p.Actions[0].Timing = new TimingCandidate { EarlyMs = 500, BaselineMs = 1000, LateMs = 2000 };
                    p.Actions[1].Timing = new TimingCandidate { EarlyMs = 1050, BaselineMs = 1100, LateMs = 3000 };
                    ProfileStore.Apply(p);
                    Expect(ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "baseline").SequenceEqual(new[] {1000.0,1100.0}), "legacy bounds still restrict exploration");
                    p.Actions[1].Timing.BaselineMs = 1050; ProfileStore.Apply(p);
                    bool rejected = false;
                    try { ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "baseline"); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "overlapping key holds accepted");
                } finally { ProfileStore.Apply(original); }
            });
            test("malformed profiles reject missing identity, unsafe keys, invalid timing and ROI", () => {
                var serializer = new JavaScriptSerializer();
                foreach (string json in new[] { "null", "{}", "{broken" })
                { bool rejected = false; try { ProfileStore.Parse(json); } catch (Exception) { rejected = true; } Expect(rejected, "malformed profile accepted"); }
                foreach (Action<ExperimentProfile> mutate in new Action<ExperimentProfile>[] {
                    p => p.SchemaVersion = 99, p => p.Id = "", p => p.BossId = "", p => p.Detector = null,
                    p => p.Actions[0].Key = "Enter", p => p.Actions[0].Group = "C", p => p.Actions[0].Timing = null,
                    p => p.Actions[0].Timing.BaselineMs = Double.NaN, p => p.Actions[0].Timing.BaselineMs = 4025,
                    p => p.Actions[0].Timing.EarlyMs = p.Actions[0].Timing.LateMs + 50,
                    p => p.Actions[1].Id = p.Actions[0].Id,
                    p => p.Detector.X = p.Detector.ReferenceWidth,
                    p => p.Detector.Threshold = 0, p => p.Detector.TimeoutMs = 0 })
                {
                    var copy = ProfileStore.Snapshot(); mutate(copy); bool rejected = false;
                    try { ProfileStore.Validate(copy); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "invalid profile accepted");
                }
            });
            test("profile groups and per-action keys drive a different three-action sequence", () => {
                string original = ProfileStore.SourcePath, file = Path.GetTempFileName();
                try
                {
                    var p = ProfileStore.Snapshot(); p.Id = "synthetic-three-action"; p.BossId = "synthetic"; p.Skill = "test";
                    p.Detector.Template = ProfileStore.TemplatePath;
                    p.Actions = new[] {
                        new ActionProfile { Id="one",Label="one",Key="Space",Group="B",MinimumMs=100,MaximumMs=20000,Timing=new TimingCandidate {EarlyMs=1000,BaselineMs=1100,LateMs=1200}},
                        new ActionProfile { Id="two",Label="two",Key="RMB",Group="A",MinimumMs=100,MaximumMs=20000,Timing=new TimingCandidate {EarlyMs=2000,BaselineMs=2100,LateMs=2200}},
                        new ActionProfile { Id="three",Label="three",Key="RMB",Group="B",MinimumMs=100,MaximumMs=20000,Timing=new TimingCandidate {EarlyMs=3000,BaselineMs=3100,LateMs=3200}}
                    };
                    File.WriteAllText(file, new JavaScriptSerializer().Serialize(p)); ProfileStore.Load(file);
                    var sink = new Sink(); var trial = Trial(Settings("a-late-b-early"), sink:sink);
                    Expect(trial.Status == "submitted-not-game-verified" && trial.DelaysMs.SequenceEqual(new double[] {1000,2200,3000}), "groups assumed index parity");
                    Expect(sink.Calls.SequenceEqual(new[] {"Space-down","Space-up","RMB-down","RMB-up","RMB-down","RMB-up"}), "keys assumed entry and supports");
                    Expect(trial.ProfileId == p.Id && trial.ProfileSnapshot.Actions.Length == 3 && trial.ProfileHash == ProfileStore.Sha256(File.ReadAllBytes(file)), "profile provenance lost");
                    using (var form = new NoticeForm(Path.GetTempPath(), true)) { form.VerifySmokeSettings(); }
                }
                finally { File.Delete(file); ProfileStore.Load(original); }
            });
            test("terminal raw log cannot be overwritten and new trials have distinct IDs", () => {
                string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var record = Settings(); NoticeRecord.Save(path, record);
                    record.Status = "detected"; NoticeRecord.Save(path, record);
                    record.Status = "submitted-not-game-verified"; NoticeRecord.Save(path, record);
                    byte[] before = File.ReadAllBytes(path); bool rejected = false;
                    try { NoticeRecord.Save(path, record); } catch (IOException) { rejected = true; }
                    Expect(rejected && before.SequenceEqual(File.ReadAllBytes(path)), "terminal raw log changed");
                    Expect(record.TrialId != Settings().TrialId, "trial ID reused");
                }
                finally { File.Delete(path); }
            });
            test("all seven combinations submit absolute deadlines and correct press/release counts", () => {
                var expected = ExperimentPlan.PatternIds.Select(id => ExperimentPlan.Resolve(ExperimentPlan.Defaults(), id)).ToArray();

                for (int p = 0; p < expected.Length; p++)
                {
                    var sink = new Sink(); var r = Trial(Settings(ExperimentPlan.PatternIds[p]), sink: sink);
                    Expect(r.Status == "submitted-not-game-verified" && r.DelaysMs.SequenceEqual(expected[p]), "wrong combination");
                    Expect(sink.Calls.Count == 2 * ExperimentPlan.Count && r.Inputs.Count == 2 * ExperimentPlan.Count && r.Inputs.Last().Attack == ExperimentPlan.Count - 1, "missing/extra action");
                    double origin = r.Samples[r.FirstMatchIndex].CaptureEndMs;
                    for (int i = 0; i < ExperimentPlan.Count; i++)
                    {
                        Expect(r.Inputs[i * 2].Key == ProfileStore.Current.Actions[i].Key && r.Inputs[i * 2].Attack == i, "wrong key/attack");
                        Expect(r.DueMs[i] == origin + expected[p][i] && r.Inputs[i * 2].BeginMs == r.DueMs[i], "not absolute notice timing");
                        Expect(r.Inputs[i * 2 + 1].Action == "up" && r.Inputs[i * 2 + 1].BeginMs - r.Inputs[i * 2].EndMs == 80, "hold/release");
                    }
                }
            });
            test("observe records all virtual presses without calling input sink", () => {
                var sink = new Sink(); var r = Trial(Settings(mode: "observe"), sink: sink);
                Expect(r.Status == "observed-no-input" && sink.Calls.Count == 0 && r.Inputs.Count == ExperimentPlan.Count && r.Inputs.All(i => i.Action == "virtual-down"), "observe submitted input");
            });
            test("API costs cannot rebase later deadlines", () => {
                var c = new Clock { Now = 1234 }; var sink = new Sink { OnSend = (key, down) => c.Now += 30 };
                var r = Trial(Settings(), c, sink);
                Expect(r.Status == "submitted-not-game-verified", "incomplete");
                for (int i = 0; i < ExperimentPlan.Count; i++) Expect(r.Inputs[i*2].BeginMs == r.DueMs[i] && r.Inputs[i*2+1].BeginMs - r.Inputs[i*2].EndMs == 80, "input drift");
            });
            test("custom candidate survives selection and log roundtrip", () => {
                var r = Settings("a-early"); double edited = r.Candidates[4].EarlyMs + 50; r.Candidates[4].EarlyMs = edited;
                r.DelaysMs = ExperimentPlan.Resolve(r.Candidates, r.Pattern); r.Recording = "OFF"; Trial(r);
                var serializer = new JavaScriptSerializer(); var copy = serializer.Deserialize<NoticeRecord>(serializer.Serialize(r));
                Expect(copy.Skill == ExperimentPlan.Skill && copy.Version == ExperimentPlan.Version && copy.Pattern == "a-early" && copy.Recording == "OFF", "metadata lost");
                Expect(copy.Candidates[4].EarlyMs == edited && copy.DelaysMs[4] == edited && copy.DueMs.Length == ExperimentPlan.Count && copy.GameOutcome == "미확인", "schedule/outcome corrupted");
                Expect(ExperimentPlan.Resolve(copy.Candidates, "baseline")[4] == ExperimentPlan.Defaults()[4].BaselineMs, "editing early moved baseline");
            });
            test("equal candidates allow fixing entry while testing support group", () => {
                var r = Settings("a-early"); r.Candidates[0].EarlyMs = r.Candidates[0].LateMs = r.Candidates[0].BaselineMs;
                r.DelaysMs = ExperimentPlan.Resolve(r.Candidates, r.Pattern); Trial(r);
                Expect(r.Status == "submitted-not-game-verified" && r.DelaysMs[0] == r.Candidates[0].BaselineMs && r.DelaysMs[2] == r.Candidates[2].EarlyMs, "cannot fix entry");
            });
            test("invalid settings rejected before capture and input", () => {
                var bad = new List<NoticeRecord>();
                foreach (double v in new[] { Double.NaN, Double.PositiveInfinity, 0, 20050, 4625 })
                { var r = Settings(); r.Candidates[0].BaselineMs = v; bad.Add(r); }
                var missing = Settings(); missing.Candidates = null; bad.Add(missing);
                var shortRows = Settings(); shortRows.Candidates = new TimingCandidate[ExperimentPlan.Count - 1]; bad.Add(shortRows);
                var nullRow = Settings(); nullRow.Candidates[ExperimentPlan.Count - 1] = null; bad.Add(nullRow);
                var order = Settings(); order.Candidates[ExperimentPlan.Count - 1].EarlyMs = 14100; bad.Add(order);
                var overlap = Settings(); overlap.Candidates[ExperimentPlan.Count - 1] = new TimingCandidate { EarlyMs = 100, BaselineMs = 100, LateMs = 100 }; overlap.DelaysMs[ExperimentPlan.Count - 1] = 100; bad.Add(overlap);
                var unknown = Settings(); unknown.Pattern = "old-a"; bad.Add(unknown);
                var stale = Settings(); stale.Mode = "invalid-mode"; bad.Add(stale);
                var wrongSkill = Settings(); wrongSkill.Skill = "other-skill"; bad.Add(wrongSkill);
                var recording = Settings(); recording.Recording = "unspecified"; bad.Add(recording);
                var mismatch = Settings(); mismatch.DelaysMs[2] += 50; bad.Add(mismatch);
                foreach (var r in bad)
                { var sink = new Sink(); Trial(r, sink: sink); Expect(r.Status == "failed" && sink.Calls.Count == 0 && r.Samples.Count == 0, "invalid setting armed"); }
            });
            test("already visible text is not a new trigger", () => {
                var sink = new Sink(); var r = Trial(Settings(), sink: sink, probe: new Probe { Visible = t => true });
                Expect(r.Status == "failed" && sink.Calls.Count == 0, "stale trigger");
            });
            test("stale notice rearms after absence", () => {
                var r = Trial(Settings(mode: "observe"), probe: new Probe { Visible = t => t < 200 || t > 400 });
                Expect(r.Status == "observed-no-input" && r.Samples[r.FirstMatchIndex].CaptureBeginMs > 400, "rearm failed");
            });
            foreach (double duration in new[] { 8.0, 1000.0/60, 1000.0/30 })
            {
                double flash = duration;
                test("transient rejected " + flash, () => {
                    var sink = new Sink(); var r = Trial(Settings(), sink: sink, probe: new Probe { Visible = t => t >= 200 && t < 200 + flash });
                    Expect(r.Status == "failed" && sink.Calls.Count == 0, "flash accepted");
                });
            }
            test("brief absence does not rearm", () => {
                var r = Trial(Settings(), probe: new Probe { Visible = t => t < 200 || t >= 280 });
                Expect(r.Status == "failed" && r.Inputs.Count == 0, "brief absence armed");
            });
            test("100ms absence and 40ms confirmation", () => {
                var samples = new List<NoticeSample>(); var edge = new NoticeEdge();
                foreach (int t in new[] { 0,100,108,148 })
                { var sample = new NoticeSample { CaptureBeginMs=t, CaptureEndMs=t, Present=t>=108 }; samples.Add(sample); Expect(edge.Push(sample, samples.Count-1, samples) == (t==148), "edge durations"); }
            });
            test("slow capture and late persistence prevent input", () => {
                var sink = new Sink(); var r = Trial(Settings(), sink: sink, probe: new Probe { CaptureCost = 101 });
                Expect(r.Status == "failed" && sink.Calls.Count == 0, "slow capture accepted");
                var c = new Clock(); sink = new Sink(); r = Trial(Settings(), c, sink, save: x => { if (x.Status == "detected") c.Now += 5000; });
                Expect(r.Status == "failed" && sink.Calls.Count == 0, "late input sent");
            });
            test("save failure before input prevents submission", () => {
                var sink = new Sink(); int saves = 0;
                var r = Trial(Settings(), sink: sink, save: x => { if (++saves == 2) throw new IOException("disk"); });
                Expect(r.Status == "failed" && sink.Calls.Count == 0, "send after save failure");
            });
            for (int attack = 0; attack < ExperimentPlan.Count; attack++)
            {
                int i = attack;
                test("cancel before action " + i, () => {
                    var sink = new Sink(); var r = Trial(Settings(), sink: sink, cancel: () => sink.Calls.Count >= i*2);
                    Expect(r.Status == "cancelled" && sink.Calls.Count == i*2, "input after cancel");
                });
                test("cancel during held action releases " + i, () => {
                    var sink = new Sink(); var r = Trial(Settings(), sink: sink, cancel: () => sink.Calls.Count >= i*2+1);
                    Expect(r.Status == "cancelled" && sink.Calls.Count == i*2+2 && r.Inputs.Last().Action == "up", "held after cancel");
                });
                test("down exception releases action " + i, () => {
                    var sink = new Sink(); sink.OnSend = (key, down) => { if (sink.Calls.Count == i*2+1) throw new IOException("down"); };
                    var r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "failed" && sink.Calls.Count == i*2+2 && r.Inputs.Last().Action == "up", "release missing");
                });
                test("release failure retried and reported at action " + i, () => {
                    var sink = new Sink(); sink.OnSend = (key, down) => { if (sink.Calls.Count >= i*2+2) sink.FailUp = true; };
                    var r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "release-failed" && sink.Calls.Count == i*2+3, "release failure hidden");
                });
            }
            foreach (string guard in new[] { "focus", "manual", "geometry" })
            {
                string g = guard;
                test(g + " before last Space suppresses last", () => {
                    var c = new Clock(); var sink = new Sink(); var probe = new Probe();
                    double guardAt = ExperimentPlan.Defaults().Last().BaselineMs;
                    if (g == "focus") sink.Foreground = () => c.Now < guardAt;
                    if (g == "manual") sink.Held = () => c.Now >= guardAt;
                    if (g == "geometry") probe.Geometry = () => { if (c.Now >= guardAt) throw new Exception("geometry"); };
                    var r = Trial(Settings(), c, sink, probe);
                    Expect(r.Status == (g == "focus" ? "cancelled" : "failed") && sink.Calls.Count == 2 * (ExperimentPlan.Count - 1), "last sent after guard");
                });
            }
            test("manual Space during owned RMB stops sequence", () => {
                var c = new Clock(); var sink = new Sink { HeldExcept = key => key == "RMB" && c.Now > ExperimentPlan.Defaults()[0].BaselineMs + 230 };
                var r = Trial(Settings(), c, sink);
                Expect(r.Status == "failed" && sink.Calls.Count == 2, "manual input ignored during hold");
            });
            test("overdue last is suppressed rather than rebased", () => {
                var c = new Clock(); var sink = new Sink(); sink.OnSend = (key, down) => { if (sink.Calls.Count == 2 * (ExperimentPlan.Count - 1)) c.Now = 15000; };
                var r = Trial(Settings(), c, sink);
                Expect(r.Status == "failed" && sink.Calls.Count == 2 * (ExperimentPlan.Count - 1) && r.Error.Contains("50ms"), "late last submitted");
            });
            test("saved custom table reloads even when only non-baseline pattern is valid", () => {
                string path = Path.GetTempFileName();
                try
                {
                    var rows = ExperimentPlan.Defaults();
                    double close = rows[0].BaselineMs + 50;
                    rows[1] = new TimingCandidate { EarlyMs = close - 100, BaselineMs = close, LateMs = ExperimentPlan.Defaults()[1].LateMs };
                    rows[ExperimentPlan.Count - 1].LateMs = 14050;
                    ExperimentPlan.Resolve(rows, "b-late");
                    File.WriteAllText(path, new JavaScriptSerializer().Serialize(rows));
                    var loaded = ExperimentPlan.LoadCandidates(path);
                    Expect(loaded[1].BaselineMs == close && loaded[ExperimentPlan.Count - 1].LateMs == 14050, "reload reset custom table");
                    for (int i=0;i<ExperimentPlan.Count;i++) Expect(loaded[i].EarlyMs==rows[i].EarlyMs && loaded[i].BaselineMs==rows[i].BaselineMs && loaded[i].LateMs==rows[i].LateMs, "row lost");
                    bool invalidBaseline=false; try { ExperimentPlan.Resolve(loaded,"baseline"); } catch (ArgumentException) { invalidBaseline=true; }
                    Expect(invalidBaseline && ExperimentPlan.Resolve(loaded,"b-late")[1]==rows[1].LateMs, "selected validation lost");
                    foreach (string bad in new[] { "null", "[]", "{broken" })
                    {
                        File.WriteAllText(path,bad); bool rejected=false;
                        try { ExperimentPlan.LoadCandidates(path); } catch (Exception) { rejected=true; }
                        Expect(rejected, "malformed saved table accepted");
                    }
                }
                finally { File.Delete(path); }
            });
            test("native submission failures and exceptions keep release guarantees", () => {
                foreach (var sink in new[] { new Sink { FailDown=true, FailKey="Space" }, new Sink { ThrowDown=true, FailKey="Space" }, new Sink { ThrowUp=true, FailKey="Space" } })
                {
                    var r=Trial(Settings(),sink:sink);
                    Expect(r.Status == (sink.ThrowUp ? "release-failed" : "failed") && sink.Calls.Last()=="Space-up", "failure/release hidden");
                }
            });
            test("template self-match and non-text backgrounds", () => {
                var matcher = new NoticeMatcher();
                using (var image = new Bitmap(ProfileStore.TemplatePath)) Expect(matcher.Score(image) > .99, "template mismatch");
                using (var image = new Bitmap(NoticeMatcher.Width,NoticeMatcher.Height))
                using (var graphics = Graphics.FromImage(image))
                { graphics.Clear(Color.Black); Expect(matcher.Score(image) < .68, "black matched"); graphics.Clear(Color.Yellow); Expect(matcher.Score(image) < .68, "yellow matched"); }
            });
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { version=ExperimentPlan.Version, skill=ExperimentPlan.Skill, passed=passed, native_input_calls=0 }));
            return 0;
        }
        private static ExperimentProfile ManualMovementFixture()
        {
            var p = StartAttackFixture(); p.AllowManualMovement = true;
            p.Preparation = p.Preparation.Where(a => a.Key == "RMB").ToArray();
            return p;
        }
        private static InputEvent[] AutomaticInputs(NoticeRecord r)
        {
            return r.StartAttackInputs.Concat(r.PreparationInputs).Concat(r.Inputs).ToArray();
        }
        private static void ManualMovementTests(Action<string, Action> test)
        {
            foreach (string key in new[] { "W", "A", "S", "D" })
            {
                string direction = key;
                test("manual " + key + " preserves automatic timing from F8 and during each hold phase", () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(ManualMovementFixture());
                        var baseline = Trial(StartAttackSettings(), probe: new Probe { Visible = t => t >= 3400 });
                        Expect(baseline.Status == "submitted-not-game-verified", "movement baseline failed");
                        var expected = AutomaticInputs(baseline);
                        foreach (double begins in new[] { 0.0, 550, 4850, 7130 })
                        {
                            var c = new Clock(); var manual = new HashSet<string>(begins == 0 ? new[] { direction } : new string[0]);
                            var sink = new Sink { DirectionExcept = owned => {
                                if (c.Now >= begins) manual.Add(direction);
                                return manual.Any(k => k != owned);
                            }, OnSend = (input, down) => {
                                if (c.Now >= begins) manual.Add(direction);
                                Expect(input == "LMB" || input == "RMB" || input == "Space", "runner injected or released manual movement");
                            } };
                            var r = Trial(StartAttackSettings(), c, sink, new Probe { Visible = t => t >= 3400 });
                            var actual = AutomaticInputs(r);
                            Expect(r.Status == "submitted-not-game-verified" && actual.Length == expected.Length
                                && actual.Zip(expected, (a, b) => a.Key == b.Key && a.Action == b.Action && a.Attack == b.Attack
                                    && a.DueMs == b.DueMs && a.BeginMs == b.BeginMs && a.EndMs == b.EndMs).All(equal => equal),
                                "manual movement changed automatic input times at " + begins);
                            Expect(manual.SetEquals(new[] { direction }), "manual direction state was changed by runner");
                        }
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            test("manual direction changes coexist with observe and never submit movement", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(ManualMovementFixture());
                    foreach (string mode in new[] { ExperimentPlan.LiveMode, "observe" })
                    {
                        var c = new Clock();
                        var sink = new Sink { DirectionExcept = owned => new[] { "W", "A", "S", "D" }[((int)c.Now / 250) % 4] != owned };
                        var r = Trial(StartAttackSettings(mode), c, sink, new Probe { Visible = t => t >= 3400 });
                        Expect(r.Status == (mode == "observe" ? "observed-no-input" : "submitted-not-game-verified")
                            && (mode != "observe" || sink.Calls.Count == 0) && r.StartAttackInputs.Count == 10 && r.PreparationInputs.Count == 4 && r.Inputs.Count == 10
                            && sink.Calls.All(call => call.StartsWith("LMB-") || call.StartsWith("RMB-") || call.StartsWith("Space-")),
                            "direction changes stopped schedule or submitted movement");
                    }
                }
                finally { ProfileStore.Apply(original); }
            });
            test("manual movement also bypasses direction guards on startup without preparation", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = ManualMovementFixture(); p.Preparation = null; ProfileStore.Apply(p);
                    var sink = new Sink { DirectionExcept = owned => true };
                    var r = Trial(StartAttackSettings(), sink: sink, probe: new Probe { Visible = t => t >= 3400 });
                    Expect(r.Status == "submitted-not-game-verified" && r.StartAttackInputs.Count == 10 && r.PreparationInputs.Count == 0 && r.Inputs.Count == 10,
                        "legacy main scheduler blocked permitted movement");
                    Expect(sink.Calls.All(call => call.StartsWith("LMB-") || call.StartsWith("RMB-") || call.StartsWith("Space-")), "legacy main scheduler touched movement");
                }
                finally { ProfileStore.Apply(original); }
            });
            foreach (string failure in new[] { "RMB", "Space", "LMB", "F9", "focus", "geometry" })
            {
                string reason = failure;
                test("manual movement keeps control and cancellation guards / " + reason, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(ManualMovementFixture()); var c = new Clock();
                        var manual = new HashSet<string>(new[] { "W", "D" });
                        var sink = new Sink { DirectionExcept = owned => manual.Any(k => k != owned), OnSend = (key, down) => {
                            Expect(key == "LMB" || key == "RMB" || key == "Space", "cleanup touched manual direction");
                        } };
                        var probe = new Probe { Visible = t => t >= 3400 };
                        if (reason == "RMB" || reason == "Space") sink.HeldExcept = owned => c.Now >= 550 && owned != reason;
                        // A manual LMB can be distinguished in the gap after the owned LMB release.
                        if (reason == "LMB") sink.LmbHeld = () => c.Now >= 650;
                        if (reason == "focus") sink.Foreground = () => c.Now < 550;
                        if (reason == "geometry") probe.Geometry = () => { if (c.Now >= 550) throw new InvalidOperationException("geometry changed"); };
                        var r = Trial(StartAttackSettings(), c, sink, probe, () => reason == "F9" && c.Now >= 550);
                        Expect(r.Status == (reason == "F9" || reason == "focus" ? "cancelled" : "failed")
                            && sink.Calls.Last() == "LMB-up" && r.Inputs.Count == 0 && r.PreparationInputs.Count == 0, "guard weakened by movement permission");
                        Expect(manual.SetEquals(new[] { "W", "D" }), "cancellation changed manual WASD");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            test("manual movement defaults, automatic-direction exclusion and snapshots stay compatible", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var serializer = new JavaScriptSerializer();
                    var strict = ManualMovementFixture(); strict.AllowManualMovement = false;
                    string omitted = serializer.Serialize(strict).Replace(",\"AllowManualMovement\":false", "").Replace("\"AllowManualMovement\":false,", "");
                    Expect(!omitted.Contains("AllowManualMovement") && !ProfileStore.Parse(omitted).AllowManualMovement, "omitted movement permission enabled");
                    ProfileStore.Apply(ProfileStore.Parse(omitted)); var sink = new Sink { DirectionExcept = owned => true };
                    var r = Trial(StartAttackSettings(), sink: sink);
                    Expect(r.Status == "failed" && sink.Calls.Count == 0, "omitted/false startup guard changed");
                    foreach (string key in new[] { "A", "D" })
                    {
                        var conflict = ManualMovementFixture(); conflict.Preparation[0].Key = key;
                        bool rejected = false; try { ProfileStore.Validate(conflict); } catch (ArgumentException) { rejected = true; }
                        Expect(rejected, "manual movement plus automatic direction accepted");
                    }
                    ProfileStore.Apply(ManualMovementFixture());
                    var record = new NoticeRecord(); var copy = serializer.Deserialize<NoticeRecord>(serializer.Serialize(record));
                    var changed = ProfileStore.Snapshot(); changed.AllowManualMovement = false; ProfileStore.Apply(changed);
                    Expect(record.ProfileSnapshot.AllowManualMovement && copy.ProfileSnapshot.AllowManualMovement && !ProfileStore.Current.AllowManualMovement, "movement snapshot lost or aliased");
                    ProfileStore.Apply(original); sink = new Sink { DirectionExcept = owned => true };
                    r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "submitted-not-game-verified", "legacy profile without startup/preparation newly blocked WASD");
                }
                finally { ProfileStore.Apply(original); }
            });
        }
        private static ExperimentProfile StartAttackFixture()
        {
            var p = AutomaticProfile();
            p.StartAttack = new StartAttackProfile { AtMs = new double[] { 500, 1050, 1700, 2300, 3100 }, HoldMs = 100 };
            return p;
        }
        private static NoticeRecord StartAttackSettings(string mode = ExperimentPlan.LiveMode)
        {
            var r = Settings(mode: mode); r.TimeoutMs = 5000; return r;
        }
        private static void StartAttackTests(Action<string, Action> test)
        {
            test("start E accepts exactly one press and rejects other keys or counts", () => {
                var p = StartAttackFixture(); p.StartAttack.Key = "E"; p.StartAttack.AtMs = new[] { 500.0 };
                ProfileStore.Validate(p);
                foreach (Action<ExperimentProfile> mutate in new Action<ExperimentProfile>[] {
                    x => x.StartAttack.Key = "Space", x => x.StartAttack.Key = "e",
                    x => x.StartAttack.AtMs = new double[0], x => x.StartAttack.AtMs = new[] { 500.0, 1000.0 }
                })
                {
                    var invalid = StartAttackFixture(); invalid.StartAttack.Key = "E"; invalid.StartAttack.AtMs = new[] { 500.0 };
                    mutate(invalid); bool rejected = false;
                    try { ProfileStore.Validate(invalid); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "invalid E start accepted");
                }
            });
            foreach (string mode in new[] { ExperimentPlan.LiveMode, "observe" })
            {
                string selectedMode = mode;
                test("start E sends or records one F8-based press before notice / " + mode, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        var p = StartAttackFixture(); p.StartAttack.Key = "E"; p.StartAttack.AtMs = new[] { 500.0 };
                        ProfileStore.Apply(p);
                        var sink = new Sink(); var r = Trial(StartAttackSettings(selectedMode), sink: sink, probe: new Probe { Visible = t => t >= 800 });
                        Expect(r.Status == (selectedMode == "observe" ? "observed-no-input" : "submitted-not-game-verified"), "E start failed: " + r.Error);
                        Expect(r.StartAttackInputs.Count == 2 && r.StartAttackInputs.All(e => e.Key == "E" && e.Attack == 0)
                            && r.StartAttackInputs[0].DueMs == 500 && r.StartAttackInputs[1].DueMs >= 600
                            && r.Samples[r.FirstMatchIndex].CaptureEndMs > r.StartAttackInputs[1].EndMs,
                            "E event or timing origin changed");
                        Expect(selectedMode == "observe" ? sink.Calls.Count == 0 && r.StartAttackInputs[0].Action == "virtual-down"
                            : sink.Calls.Take(2).SequenceEqual(new[] { "E-down", "E-up" }), "E input mode changed");
                        var copy = new JavaScriptSerializer().Deserialize<NoticeRecord>(new JavaScriptSerializer().Serialize(r));
                        Expect(copy.ProfileSnapshot.StartAttack.Key == "E" && copy.StartAttackInputs[1].Key == "E"
                            && copy.Inputs.Count == r.Inputs.Count, "E provenance did not survive raw record round trip");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            test("start E blocks early notice and releases on interruption", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = StartAttackFixture(); p.StartAttack.Key = "E"; p.StartAttack.AtMs = new[] { 500.0 };
                    ProfileStore.Apply(p);
                    var sink = new Sink(); var r = Trial(StartAttackSettings(), sink: sink, probe: new Probe { Visible = t => t >= 520 });
                    Expect(r.Status == "failed" && r.Error.Contains("해제 완료 전에") && r.Inputs.Count == 0
                        && sink.Calls.SequenceEqual(new[] { "E-down", "E-up" }), "early notice entered schedule or leaked E");
                    var c = new Clock(); sink = new Sink();
                    r = Trial(StartAttackSettings(), c, sink, new Probe { Visible = t => t >= 800 }, () => c.Now >= 550);
                    Expect(r.Status == "cancelled" && sink.Calls.SequenceEqual(new[] { "E-down", "E-up" })
                        && r.Inputs.Count == 0, "F9 did not release owned E");
                    sink = new Sink { EHeld = () => true };
                    r = Trial(StartAttackSettings(), sink: sink);
                    Expect(r.Status == "failed" && sink.Calls.Count == 0, "preheld E accepted");
                    c = new Clock(); sink = new Sink { EHeld = () => c.Now >= 1200 };
                    r = Trial(StartAttackSettings(), c, sink, new Probe { Visible = t => t >= 800 });
                    Expect(r.Status == "failed" && r.StartAttackInputs.Count == 2 && r.Inputs.Count == 0,
                        "manual E after startup was not guarded");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("start LMB validates exactly five ordered finite grid timings within timeout", () => {
                foreach (Action<ExperimentProfile> mutate in new Action<ExperimentProfile>[] {
                    p => p.StartAttack.AtMs = null, p => p.StartAttack.AtMs = new double[4],
                    p => p.StartAttack.AtMs[0] = Double.NaN, p => p.StartAttack.AtMs[0] = Double.PositiveInfinity,
                    p => p.StartAttack.AtMs[0] = 0, p => p.StartAttack.AtMs[0] = 525,
                    p => p.StartAttack.AtMs[1] = 450, p => p.StartAttack.AtMs[1] = 550,
                    p => p.StartAttack.AtMs[4] = p.Detector.TimeoutMs - 100,
                    p => p.StartAttack.HoldMs = 0, p => p.StartAttack.HoldMs = Double.NaN,
                    p => p.StartAttack.HoldMs = Double.PositiveInfinity, p => p.StartAttack.HoldMs = 80,
                    p => p.StartAttack.HoldMs = 2050 })
                {
                    var p = StartAttackFixture(); mutate(p); bool rejected = false;
                    try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "invalid start attack accepted");
                }
            });
            foreach (string mode in new[] { ExperimentPlan.LiveMode, "observe" })
            {
                string selectedMode = mode;
                test("start LMB five presses retain runner origin and separate notice schedule / " + mode, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(StartAttackFixture());
                        var c = new Clock { Now = 1234 }; bool heldLmb = false;
                        var sink = new Sink { LmbHeld = () => heldLmb, OnSend = (key, down) => { if (key == "LMB") heldLmb = down; c.Now += 30; } };
                        var r = Trial(StartAttackSettings(selectedMode), c, sink, new Probe { Visible = t => t >= 3400 });
                        Expect(r.Status == (selectedMode == "observe" ? "observed-no-input" : "submitted-not-game-verified"), "start schedule failed: " + r.Error);
                        Expect(r.StartAttackInputs.Count == 10 && r.Inputs.Count == 10 && r.PreparationInputs.Count == 12 && r.ActionIds.Length == 5 && !heldLmb,
                            "start inputs polluted preparation/results or leaked LMB");
                        for (int i = 0; i < 5; i++)
                        {
                            var down = r.StartAttackInputs[i * 2]; var up = r.StartAttackInputs[i * 2 + 1];
                            Expect(down.Key == "LMB" && down.Attack == i && down.Action == (selectedMode == "observe" ? "virtual-down" : "down")
                                && up.Action == (selectedMode == "observe" ? "virtual-up" : "up") && up.Attack == i, "start event order changed");
                            Expect(down.DueMs == ProfileStore.Current.StartAttack.AtMs[i] && down.BeginMs >= down.DueMs && down.BeginMs - down.DueMs <= 10
                                && up.DueMs == down.EndMs + 100 && up.BeginMs >= up.DueMs && up.BeginMs - up.DueMs <= 10, "start origin/hold changed");
                        }
                        double noticeOrigin = r.Samples[r.FirstMatchIndex].CaptureEndMs;
                        Expect(r.Samples.Any(s => s.CaptureBeginMs > 500 && s.CaptureBeginMs < 3100)
                            && r.DueMs[0] == noticeOrigin + r.DelaysMs[0] && noticeOrigin > r.StartAttackInputs.Last().EndMs,
                            "capture paused during start or notice/main origin changed");
                        Expect(selectedMode != "observe" || sink.Calls.Count == 0, "observe submitted native input");
                        var serializer = new JavaScriptSerializer(); var copy = serializer.Deserialize<NoticeRecord>(serializer.Serialize(r));
                        var p = ProfileStore.Snapshot(); p.StartAttack.AtMs[0] = 550; ProfileStore.Apply(p);
                        Expect(copy.ProfileSnapshot.StartAttack.AtMs[0] == 500 && copy.StartAttackInputs.Count == 10, "start snapshot/inputs lost or aliased");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            foreach (double appears in new[] { 200.0, 520.0 })
            {
                double onset = appears;
                test("notice confirmed before fifth LMB aborts remaining start inputs / " + appears, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(StartAttackFixture()); var sink = new Sink();
                        var r = Trial(StartAttackSettings(), sink: sink, probe: new Probe { Visible = t => t >= onset });
                        Expect(r.Status == "failed" && r.Error.Contains("다섯 번") && r.ConfirmedIndex >= 0 && r.Inputs.Count == 0 && r.PreparationInputs.Count == 0,
                            "early notice entered preparation/main schedule");
                        Expect(r.StartAttackInputs.Count == (onset < 500 ? 0 : 2) && (onset < 500 || sink.Calls.Last() == "LMB-up"), "pending LMBs submitted or owned LMB leaked");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            test("first notice match before final LMB release rejects even when confirmation follows release", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(StartAttackFixture()); var sink = new Sink();
                    var r = Trial(StartAttackSettings(), sink: sink, probe: new Probe { Visible = t => t >= 3180 });
                    Expect(r.StartAttackInputs.Count == 10 && r.FirstMatchIndex >= 0 && r.ConfirmedIndex >= 0, "boundary fixture did not finish five LMB releases and confirm notice");
                    double releaseEnd = r.StartAttackInputs.Last().EndMs;
                    Expect(r.Samples[r.FirstMatchIndex].CaptureEndMs < releaseEnd && r.Samples[r.ConfirmedIndex].CaptureEndMs >= releaseEnd,
                        "fixture did not straddle final release");
                    Expect(r.Status == "failed" && r.Error.Contains("해제 완료 전에") && r.PreparationInputs.Count == 0 && r.Inputs.Count == 0
                        && sink.Calls.Last() == "LMB-up", "late confirmation accepted a notice that began before startup completed");
                }
                finally { ProfileStore.Apply(original); }
            });
            foreach (string failure in new[] { "F9", "focus", "geometry", "manual-control", "manual-direction", "manual-LMB", "fail-down", "throw-down", "release-failed" })
            {
                string reason = failure;
                test("start attack interruption releases owned LMB / " + reason, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(StartAttackFixture()); var c = new Clock(); var sink = new Sink(); var probe = new Probe { Visible = t => t >= 3400 };
                        Func<bool> cancel = () => reason == "F9" && c.Now >= 550;
                        if (reason == "focus") sink.Foreground = () => c.Now < 550;
                        if (reason == "geometry") probe.Geometry = () => { if (c.Now >= 550) throw new InvalidOperationException("geometry changed"); };
                        if (reason == "manual-control") sink.Held = () => c.Now >= 550;
                        if (reason == "manual-direction") sink.DirectionExcept = ignored => c.Now >= 550;
                        if (reason == "manual-LMB") sink.LmbHeld = () => c.Now >= 650;
                        if (reason == "fail-down") { sink.FailDown = true; sink.FailKey = "LMB"; }
                        if (reason == "throw-down") { sink.ThrowDown = true; sink.FailKey = "LMB"; }
                        if (reason == "release-failed") { sink.FailUp = true; sink.FailKey = "LMB"; }
                        var r = Trial(StartAttackSettings(), c, sink, probe, cancel);
                        Expect(r.Status == (reason == "F9" || reason == "focus" ? "cancelled" : reason == "release-failed" ? "release-failed" : "failed"), "wrong stop status: " + r.Status);
                        Expect(sink.Calls.First() == "LMB-down" && sink.Calls.Last() == "LMB-up" && r.Inputs.Count == 0 && r.PreparationInputs.Count == 0,
                            "interruption leaked LMB or continued to later schedule");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            test("start config rejects preheld LMB and guards LMB after startup while legacy still allows it", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(StartAttackFixture()); var sink = new Sink { LmbHeld = () => true };
                    var r = Trial(StartAttackSettings(), sink: sink);
                    Expect(r.Status == "failed" && sink.Calls.Count == 0, "preheld LMB accepted");
                    var c = new Clock(); sink = new Sink { LmbHeld = () => c.Now >= 4200 };
                    r = Trial(StartAttackSettings(), c, sink, new Probe { Visible = t => t >= 3400 });
                    Expect(r.Status == "failed" && r.StartAttackInputs.Count == 10 && r.Inputs.Count == 0, "LMB after startup was not guarded");
                    ProfileStore.Apply(original); sink = new Sink { LmbHeld = () => true };
                    r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "submitted-not-game-verified" && r.StartAttackInputs.Count == 0 && !sink.Calls.Any(call => call.StartsWith("LMB")), "legacy manual LMB behavior changed");
                }
                finally { ProfileStore.Apply(original); }
            });
        }
        private static ExperimentProfile StartInputFixture()
        {
            var p = AutomaticProfile(); p.SchemaVersion = 3; p.StartAttack = null; p.AllowManualMovement = false;
            p.StartInputs = new[] {
                new StartInputPress { Id = "forward-first", Label = "첫 전진", Key = "W", AtMs = 500, HoldMs = 600 },
                new StartInputPress { Id = "forward-dodge", Label = "회피 전진", Key = "W", AtMs = 1500, HoldMs = 500 },
                new StartInputPress { Id = "dodge-with-w", Label = "전진 중 회피", Key = "RMB", AtMs = 1600, HoldMs = 100 },
                new StartInputPress { Id = "dodge-after-w", Label = "전진 후 회피", Key = "RMB", AtMs = 2200, HoldMs = 100 }
            };
            return p;
        }
        private static void StartInputTests(Action<string, Action> test)
        {
            test("F8 start inputs require schema 3, distinct holds and no legacy start/manual movement", () => {
                ProfileStore.Validate(StartInputFixture());
                foreach (Action<ExperimentProfile> change in new Action<ExperimentProfile>[] {
                    p => p.SchemaVersion = 2, p => p.StartAttack = new StartAttackProfile { Key = "E", AtMs = new[] { 500.0 }, HoldMs = 100 },
                    p => p.AllowManualMovement = true, p => p.StartInputs[0].Key = "Space", p => p.StartInputs[0].AtMs = 50,
                    p => p.StartInputs[0].HoldMs = 0, p => p.StartInputs[0].HoldMs = Double.NaN,
                    p => p.StartInputs[1].AtMs = 1000, p => p.StartInputs[2].AtMs = 1625,
                    p => p.StartInputs[3].Id = p.StartInputs[2].Id
                })
                {
                    var invalid = StartInputFixture(); change(invalid); bool rejected = false;
                    try { ProfileStore.Validate(invalid); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "invalid F8 start input accepted");
                }
            });
            foreach (string mode in new[] { ExperimentPlan.LiveMode, "observe" })
            {
                string selected = mode;
                test("F8 W/RMB start sequence and separate notice-relative results / " + mode, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(StartInputFixture()); var c = new Clock(); var sink = new Sink(); var held = new HashSet<string>();
                        sink.OnSend = (key, down) => { if (down) Expect(held.Add(key), "duplicate start down"); else held.Remove(key); };
                        sink.HeldExcept = ignored => held.Any(k => (k == "RMB" || k == "Space") && k != ignored);
                        sink.DirectionExcept = ignored => held.Any(k => k == "W" && k != ignored);
                        var r = Trial(StartAttackSettings(selected), c, sink, new Probe { Visible = t => t >= 2600 });
                        Expect(r.Status == (selected == "observe" ? "observed-no-input" : "submitted-not-game-verified")
                            && held.Count == 0 && r.StartInputs.Count == 8 && r.StartAttackInputs.Count == 0 && r.Inputs.Count == 10,
                            "F8 start sequence failed: " + r.Error);
                        var downs = r.StartInputs.Where(e => e.Action.EndsWith("down")).ToArray();
                        Expect(downs.Select(e => e.Key).SequenceEqual(new[] { "W", "W", "RMB", "RMB" })
                            && downs.Select(e => e.DueMs).SequenceEqual(new[] { 500.0, 1500, 1600, 2200 }), "start origin or press order changed");
                        Expect(r.StartInputs.Where(e => e.Key == "W").Last().EndMs < r.StartInputs.Where(e => e.Key == "RMB").Last().BeginMs
                            && r.DueMs[0] == r.Samples[r.FirstMatchIndex].CaptureEndMs + r.DelaysMs[0]
                            && r.Samples[r.FirstMatchIndex].CaptureEndMs > r.StartInputs.Last().EndMs, "start/notice origins mixed");
                        Expect(selected != "observe" || sink.Calls.Count == 0, "observe submitted native start input");
                        var copy = new JavaScriptSerializer().Deserialize<NoticeRecord>(new JavaScriptSerializer().Serialize(r));
                        Expect(copy.ProfileSnapshot.StartInputs.Length == 4 && copy.StartInputs.Count == 8
                            && copy.StartTimingOrigin.StartsWith("F8"), "F8 start plan or events absent from raw record");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            foreach (string reason in new[] { "early-notice", "F9", "focus", "timeout", "send-error", "preheld-W", "preheld-RMB" })
            {
                string selected = reason;
                test("F8 start W/RMB cleanup / " + reason, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(StartInputFixture()); var c = new Clock(); var sink = new Sink(); var held = new HashSet<string>();
                        sink.OnSend = (key, down) => { if (down) held.Add(key); else held.Remove(key); };
                        sink.HeldExcept = ignored => held.Any(k => k == "RMB" && k != ignored) || (selected == "preheld-RMB" && c.Now == 0);
                        sink.DirectionExcept = ignored => held.Any(k => k == "W" && k != ignored) || (selected == "preheld-W" && c.Now == 0);
                        if (selected == "focus") sink.Foreground = () => c.Now < 1650;
                        if (selected == "send-error") { sink.FailDown = true; sink.FailKey = "RMB"; }
                        var probe = new Probe { Visible = t => t >= (selected == "early-notice" ? 1620 : selected == "timeout" ? 100000 : 2600) };
                        var r = Trial(StartAttackSettings(), c, sink, probe, () => selected == "F9" && c.Now >= 1650);
                        Expect(r.Status == (selected == "F9" || selected == "focus" ? "cancelled" : "failed")
                            && held.Count == 0 && r.Inputs.Count == 0, "F8 start cleanup failed: " + r.Error);
                        if (selected == "early-notice") Expect(r.Error.Contains("해제 완료 전에") && r.StartInputs.Count < 8,
                            "early notice submitted pending start inputs");
                        if (selected.StartsWith("preheld")) Expect(sink.Calls.Count == 0, "preheld key accepted");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
        }
        private static void ActionHoldTests(Action<string, Action> test)
        {
            test("omitted action holds retain 80ms and legacy source hashes and snapshots", () => {
                string source = ProfileStore.SourcePath, file = Path.GetTempFileName();
                try
                {
                    var serializer = new JavaScriptSerializer();
                    var p = ProfileStore.Snapshot(); p.Detector.Template = ProfileStore.TemplatePath;
                    string legacy = serializer.Serialize(p).Replace(",\"HoldMs\":80", "").Replace("\"HoldMs\":80,", "");
                    Expect(!legacy.Contains("HoldMs"), "legacy fixture still contains holds");
                    File.WriteAllText(file, legacy);
                    string hash = ProfileStore.Sha256(File.ReadAllBytes(file));
                    ProfileStore.Load(file);
                    Expect(ProfileStore.Hash == hash && ProfileStore.SourceHash == hash && ProfileStore.Current.Actions.All(a => a.HoldMs == 80), "legacy source hash or default hold changed");
                    var r = Trial(Settings());
                    for (int i = 0; i < r.ActionIds.Length; i++)
                        Expect(r.Inputs[i * 2 + 1].BeginMs - r.Inputs[i * 2].EndMs == 80, "legacy release changed");
                    string oldRecord = serializer.Serialize(r).Replace(",\"HoldMs\":80", "").Replace("\"HoldMs\":80,", "");
                    var read = serializer.Deserialize<NoticeRecord>(oldRecord);
                    Expect(read.ProfileHash == hash && read.SourceProfileHash == hash && read.ProfileSnapshot.Actions.All(a => a.HoldMs == 80), "legacy record provenance or hold changed on read");
                    var snapshot = ProfileStore.Snapshot(); snapshot.Actions[0].HoldMs = 150;
                    Expect(ProfileStore.Current.Actions[0].HoldMs == 80 && ProfileStore.Hash == hash && File.ReadAllText(file) == legacy, "snapshot changed legacy source or active profile");
                }
                finally { ProfileStore.Load(source); File.Delete(file); }
            });
            foreach (bool preparation in new[] { false, true })
            {
                bool usePreparation = preparation;
                test("per-action holds survive API duration and snapshots / preparation=" + usePreparation, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        var p = usePreparation ? AutomaticProfile() : ProfileStore.Snapshot();
                        double[] holds = { 100, 150, 200, 250, 200, 80 };
                        for (int i = 0; i < p.Actions.Length; i++) p.Actions[i].HoldMs = holds[i];
                        ProfileStore.Apply(p);
                        var c = new Clock(); var sink = new Sink { OnSend = (key, down) => c.Now += 30 };
                        var r = Trial(Settings(), c, sink);
                        Expect(r.Status == "submitted-not-game-verified", "variable holds failed: " + r.Error);
                        for (int i = 0; i < p.Actions.Length; i++)
                            Expect(r.Inputs[i * 2].BeginMs == r.DueMs[i] && r.Inputs[i * 2 + 1].BeginMs - r.Inputs[i * 2].EndMs == holds[i], "hold is not measured after API return or later down drifted");
                        if (usePreparation)
                        {
                            var observeSink = new Sink(); var observed = Trial(Settings(mode: "observe"), sink: observeSink);
                            Expect(observed.Status == "observed-no-input" && observeSink.Calls.Count == 0, "variable-hold observe sent input");
                            for (int i = 0; i < p.Actions.Length; i++)
                                Expect(observed.Inputs[i * 2 + 1].DueMs - observed.Inputs[i * 2].EndMs == holds[i], "virtual release lost configured hold");
                        }
                        var serializer = new JavaScriptSerializer();
                        var copy = serializer.Deserialize<NoticeRecord>(serializer.Serialize(r));
                        p = ProfileStore.Snapshot(); p.Actions[0].HoldMs = 50; ProfileStore.Apply(p);
                        Expect(copy.ProfileSnapshot.Actions.Select(a => a.HoldMs).SequenceEqual(holds.Take(p.Actions.Length)) && r.ProfileSnapshot.Actions[0].HoldMs == 100,
                            "saved holds lost or aliased active profile");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            test("action hold validation and spacing use the preceding action duration", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, -50.0, 0, 25, 81, 2050 })
                    {
                        var bad = ProfileStore.Snapshot(); bad.Actions[0].HoldMs = invalid;
                        bool rejected = false; try { ProfileStore.Validate(bad); } catch (ArgumentException) { rejected = true; }
                        Expect(rejected, "invalid action hold accepted: " + invalid);
                    }
                    var p = ProfileStore.Snapshot(); p.Actions = p.Actions.Take(2).ToArray();
                    p.Actions[0].Timing = new TimingCandidate { EarlyMs = 1000, BaselineMs = 1000, LateMs = 1000 };
                    p.Actions[1].Timing = new TimingCandidate { EarlyMs = 1100, BaselineMs = 1100, LateMs = 1100 };
                    p.Actions[0].HoldMs = 150; p.Actions[1].HoldMs = 50; ProfileStore.Apply(p);
                    bool overlap = false; try { ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "baseline"); } catch (ArgumentException) { overlap = true; }
                    Expect(overlap, "long preceding hold overlapped next action");
                    p.Actions[0].HoldMs = 50; p.Actions[1].HoldMs = 200; ProfileStore.Apply(p);
                    Expect(ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "baseline")[1] == 1100, "next action duration incorrectly constrained preceding gap");
                    // Planned equality can still overlap after a slow OS call. Abort before a second control down.
                    p.Actions[0].HoldMs = 100;
                    p.Preparation = new[] { new PreparationPress { Id = "prep", Label = "prep", Key = "RMB", AtMs = 500, HoldMs = 150 } };
                    ProfileStore.Apply(p);
                    var c = new Clock(); var sink = new Sink { OnSend = (key, down) => c.Now += 30 };
                    var r = Trial(Settings(), c, sink);
                    Expect(r.Status == "failed" && r.Inputs.Count == 2 && r.Inputs[0].Action == "down" && r.Inputs[1].Action == "up"
                        && !sink.Calls.Contains("Space-down"), "API-extended overlap submitted second control or leaked first key");
                }
                finally { ProfileStore.Apply(original); }
            });
        }
        private static ExperimentProfile AutomaticProfile()
        {
            var p = ProfileStore.Snapshot(); p.ManualPreparationRmbUntilMs = 0;
            p.Detector.ConfirmationMs = 40;
            p.Actions = p.Actions.Take(5).ToArray();
            double[] baseline = { 3700, 6550, 7650, 9100, 10450 };
            for (int i = 0; i < p.Actions.Length; i++)
                p.Actions[i].Timing = new TimingCandidate { EarlyMs = baseline[i] - 100, BaselineMs = baseline[i], LateMs = baseline[i] + 100 };
            string[] keys = { "A", "A", "RMB", "D", "RMB", "A" };
            double[] at = { 550, 1350, 1400, 2250, 2350, 3450 }, hold = { 650, 800, 150, 1200, 250, 550 };
            p.Preparation = keys.Select((key, i) => new PreparationPress { Id = "prep-" + i, Label = "preparation " + i,
                Key = key, AtMs = at[i], HoldMs = hold[i] }).ToArray();
            return p;
        }
        private static ExperimentProfile AuxiliaryProfile()
        {
            var p = ProfileStore.Snapshot();
            p.SchemaVersion = 2; p.ManualPreparationRmbUntilMs = 0; p.Preparation = null; p.StartAttack = null;
            p.AuxiliaryInputs = new[] {
                new AuxiliaryPress { Id = "recover-space", Label = "3~4타 회복 Space", Key = "Space", AtMs = 7200, HoldMs = 100, Enabled = true },
                new AuxiliaryPress { Id = "disabled-lmb", Label = "미측정 LMB", Key = "LMB", AtMs = 7250, HoldMs = 100, Enabled = false }
            };
            return p;
        }
        private static void AuxiliaryInputTests(Action<string, Action> test)
        {
            test("auxiliary profiles require v2 and validate enabled schedule without executing disabled rows", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = AuxiliaryProfile(); p.SchemaVersion = 1;
                    bool rejected = false; try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "v1 profile accepted auxiliary rows");
                    p = AuxiliaryProfile(); p.ManualPreparationRmbUntilMs = 1000;
                    rejected = false; try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "manual preparation accepted active auxiliary input");
                    p = AuxiliaryProfile(); p.AuxiliaryInputs[1].Key = "A";
                    rejected = false; try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "disabled auxiliary row bypassed structural validation");
                    p = AuxiliaryProfile(); p.AuxiliaryInputs[0].AtMs = p.Actions[3].Timing.BaselineMs;
                    rejected = false; try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "enabled auxiliary hold overlapped selected main action");
                    p = AuxiliaryProfile(); p.Preparation = new[] { new PreparationPress { Id = "prep", Label = "준비 회피", Key = "RMB", AtMs = 1000, HoldMs = 100 } };
                    p.AuxiliaryInputs[0].AtMs = 1050;
                    rejected = false; try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "enabled auxiliary hold overlapped automatic preparation");
                    p.AuxiliaryInputs[0].Enabled = false; ProfileStore.Apply(p);
                    Expect(ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "baseline").Length == p.Actions.Length,
                        "disabled auxiliary collision affected execution");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("auxiliary input interleaves between scored actions and keeps result length and logs separate", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(AuxiliaryProfile());
                    var sink = new Sink(); var r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "submitted-not-game-verified" && r.ActionIds.Length == 6 && r.DelaysMs.Length == 6,
                        "auxiliary input changed scored result shape: " + r.Error);
                    Expect(r.Inputs.Count == 12 && r.AuxiliaryInputs.Count == 2 && r.PreparationInputs.Count == 0,
                        "auxiliary events polluted scored or preparation logs");
                    Expect(r.AuxiliaryInputs.All(e => e.Attack == 0 && e.Key == "Space")
                        && r.AuxiliaryInputs.Select(e => e.Action).SequenceEqual(new[] { "down", "up" }), "auxiliary event identity or order changed");
                    int auxiliaryDown = sink.Calls.IndexOf("Space-down", 8);
                    Expect(auxiliaryDown == 8 && sink.Calls[auxiliaryDown + 1] == "Space-up", "auxiliary input did not run between fourth and fifth scored actions");
                    var copy = new JavaScriptSerializer().Deserialize<NoticeRecord>(new JavaScriptSerializer().Serialize(r));
                    Expect(copy.ProfileSnapshot.SchemaVersion == 2 && copy.ProfileSnapshot.AuxiliaryInputs.Length == 2
                        && !copy.ProfileSnapshot.AuxiliaryInputs[1].Enabled && copy.AuxiliaryInputs.Count == 2,
                        "auxiliary plan or immutable raw events were lost");
                    var observed = Trial(Settings(mode: "observe"), sink: new Sink());
                    Expect(observed.Status == "observed-no-input" && observed.Inputs.Count == 12 && observed.AuxiliaryInputs.Count == 2
                        && observed.AuxiliaryInputs.All(e => e.Action == "virtual-down" || e.Action == "virtual-up"),
                        "observe mode sent input or lost auxiliary down/up");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("auxiliary hold survives Send delay and releases before an equal-boundary main press", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = AuxiliaryProfile();
                    p.AuxiliaryInputs = new[] { new AuxiliaryPress { Id = "boundary", Label = "경계 보조", Key = "Space", AtMs = 1900, HoldMs = 100, Enabled = true } };
                    ProfileStore.Apply(p);
                    var c = new Clock(); var sink = new Sink { OnSend = (key, down) => { if (down) c.Now += 30; } };
                    var r = Trial(Settings(), c, sink);
                    Expect(r.Status == "submitted-not-game-verified", "Send delay made equal-boundary control overlap unsafe: " + r.Error);
                    Expect(sink.Calls.Take(4).SequenceEqual(new[] { "Space-down", "Space-up", "RMB-down", "RMB-up" }),
                        "owned auxiliary key was not released before the main press");
                    Expect(r.AuxiliaryInputs[1].DueMs - r.AuxiliaryInputs[0].EndMs == 100
                        && r.Inputs[0].BeginMs - r.Inputs[0].DueMs == 30, "hold-after-Send or bounded late main timing changed");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("enabled auxiliary LMB is guarded and recorded as a program-owned control", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = AuxiliaryProfile();
                    p.AuxiliaryInputs = new[] { new AuxiliaryPress { Id = "lmb", Label = "회복 LMB", Key = "LMB", AtMs = 1900, HoldMs = 100, Enabled = true } };
                    ProfileStore.Apply(p);
                    var held = new Sink { LmbHeld = () => true }; var rejected = Trial(Settings(), sink: held);
                    Expect(rejected.Status == "failed" && rejected.Samples.Count == 0 && held.Calls.Count == 0,
                        "physical LMB was not guarded before an automatic auxiliary LMB");
                    var sink = new Sink(); var r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "submitted-not-game-verified" && r.AuxiliaryInputs.Count == 2
                        && r.AuxiliaryInputs.All(e => e.Key == "LMB") && sink.Calls.Take(2).SequenceEqual(new[] { "LMB-down", "LMB-up" }),
                        "automatic auxiliary LMB was not owned, released and logged separately");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("auxiliary interruption releases the owned key into the auxiliary log only", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(AuxiliaryProfile());
                    var c = new Clock(); var sink = new Sink(); double cancelAt = Double.PositiveInfinity;
                    var r = Trial(Settings(), c, sink, cancel: () => c.Now >= cancelAt, save: record => {
                        if (record.Status == "detected") cancelAt = record.Samples[record.FirstMatchIndex].CaptureEndMs + 7250;
                    });
                    Expect(r.Status == "cancelled" && r.AuxiliaryInputs.Count == 2 && r.AuxiliaryInputs.Last().Action == "up",
                        "cancelled auxiliary input was not released and recorded: " + r.Error);
                    Expect(r.Inputs.Count == 8 && r.PreparationInputs.Count == 0 && sink.Calls.Last() == "Space-up",
                        "auxiliary cleanup polluted scored/preparation events or left the key held");
                    var p = AuxiliaryProfile();
                    p.AuxiliaryInputs = new[] { new AuxiliaryPress { Id = "send-failure", Label = "전송 실패", Key = "Space", AtMs = 1900, HoldMs = 100, Enabled = true } };
                    ProfileStore.Apply(p); sink = new Sink();
                    sink.OnSend = (key, down) => { if (key == "Space" && down) { sink.FailKey = "Space"; sink.ThrowDown = true; } };
                    r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "failed" && r.Inputs.Count == 0 && r.AuxiliaryInputs.Count == 2
                        && r.AuxiliaryInputs.Last().Action == "up" && sink.Calls.SequenceEqual(new[] { "Space-down", "Space-up" }),
                        "auxiliary Send failure omitted cleanup or polluted scored events");
                }
                finally { ProfileStore.Apply(original); }
            });
        }
        private static void AutomaticPreparationTests(Action<string, Action> test)
        {
            test("automatic preparation validates key conflicts, finite timing and manual exclusion", () => {
                var original = ProfileStore.Snapshot();
                foreach (string scenario in new[] { "manual", "same-key", "opposite", "duplicate-id", "invalid-key", "nan", "infinity", "early", "short", "grid", "null-item" })
                {
                    var p = AutomaticProfile();
                    if (scenario == "manual") p.ManualPreparationRmbUntilMs = 3000;
                    if (scenario == "same-key") p.Preparation[1].AtMs = 600;
                    if (scenario == "opposite") { p.Preparation[1].AtMs = 600; p.Preparation[1].Key = "D"; }
                    if (scenario == "duplicate-id") p.Preparation[1].Id = p.Preparation[0].Id;
                    if (scenario == "invalid-key") p.Preparation[0].Key = "Space";
                    if (scenario == "nan") p.Preparation[0].AtMs = Double.NaN;
                    if (scenario == "infinity") p.Preparation[0].HoldMs = Double.PositiveInfinity;
                    if (scenario == "early") p.Preparation[0].AtMs = 50;
                    if (scenario == "short") p.Preparation[0].HoldMs = 0;
                    if (scenario == "grid") p.Preparation[0].AtMs = 551;
                    if (scenario == "null-item") p.Preparation[0] = null;
                    bool rejected = false; try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "accepted invalid preparation: " + scenario);
                }
                try
                {
                    var p = AutomaticProfile(); p.Preparation[4].AtMs = 3600; p.Preparation[4].HoldMs = 100;
                    ProfileStore.Apply(p);
                    bool rejected = false; try { ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "baseline"); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected && ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "entry-late")[0] == 3800, "selected RMB collision validation is wrong");
                    p = AutomaticProfile(); p.Preparation[5].HoldMs = 3100; ProfileStore.Apply(p);
                    rejected = false; try { ExperimentPlan.Resolve(ExperimentPlan.Defaults(), "baseline"); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "direction continued into next support");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("automatic preparation overlaps direction and RMB, preserves five main actions and event order", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(AutomaticProfile());
                    var c = new Clock { Now = 1234 }; var sink = new Sink(); var held = new HashSet<string>();
                    sink.OnSend = (key, down) => { if (down) Expect(held.Add(key), "duplicate down"); else held.Remove(key); };
                    sink.HeldExcept = ignored => held.Any(k => (k == "RMB" || k == "Space") && k != ignored);
                    sink.DirectionExcept = ignored => held.Any(k => (k == "A" || k == "D") && k != ignored);
                    var r = Trial(Settings(), c, sink);
                    Expect(r.Status == "submitted-not-game-verified" && held.Count == 0, "overlap not safely completed: " + r.Error);
                    Expect(r.Inputs.Count == 10 && r.ActionIds.Length == 5 && r.DelaysMs.Length == 5 && r.PreparationInputs.Count == 12, "preparation polluted five-action outcome");
                    double origin = r.Samples[r.FirstMatchIndex].CaptureEndMs;
                    for (int i = 0; i < ProfileStore.Current.Preparation.Length; i++)
                    {
                        var p = ProfileStore.Current.Preparation[i]; var events = r.PreparationInputs.Where(e => e.Attack == i).ToArray();
                        Expect(events[0].BeginMs == origin + p.AtMs && events[1].BeginMs == origin + p.AtMs + p.HoldMs, "preparation deadline rebased");
                    }
                    int dUp = sink.Calls.IndexOf("D-up");
                    Expect(sink.Calls[dUp + 1] == "A-down", "equal-time direction switch did not release first");
                    int entry = sink.Calls.FindLastIndex(call => call == "RMB-down");
                    Expect(sink.Calls[entry - 1] == "A-down" && sink.Calls[entry + 2] == "A-up", "entry lost held direction");
                    Expect(r.GameOutcome == "미확인", "input submission became game verification");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("automatic main holds retain 80ms after API return while preparation releases keep origin", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = AutomaticProfile(); p.Preparation[5].HoldMs = 350; ProfileStore.Apply(p);
                    var c = new Clock(); var sink = new Sink { OnSend = (key, down) => c.Now += 30 };
                    var r = Trial(Settings(), c, sink);
                    Expect(r.Status == "submitted-not-game-verified", "API duration interrupted valid schedule: " + r.Error);
                    for (int i = 0; i < 5; i++)
                        Expect(r.Inputs[i * 2].BeginMs == r.DueMs[i] && r.Inputs[i * 2 + 1].DueMs - r.Inputs[i * 2].EndMs == 80,
                            "main hold or down origin changed");
                    // A release at 3800 can interleave between main down and its release at 3810.
                    var directionUp = r.PreparationInputs.Last();
                    Expect(directionUp.DueMs == r.Samples[r.FirstMatchIndex].CaptureEndMs + 3800 && directionUp.BeginMs == directionUp.DueMs,
                        "direction release was blocked by main hold");
                    Expect(r.Inputs[1].BeginMs >= r.Inputs[1].DueMs && r.Inputs[1].BeginMs - r.Inputs[1].DueMs <= 50, "interleaved release missed safety bound");
                }
                finally { ProfileStore.Apply(original); }
            });
            test("automatic preparation observe emits virtual down/up only and saves immutable preparation snapshot", () => {
                string original = ProfileStore.SourcePath;
                string directory = Path.Combine(Path.GetTempPath(), "automatic-preparation-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ProfileStore.Apply(AutomaticProfile());
                    string first = Path.Combine(directory, "first.json"), second = Path.Combine(directory, "second.json");
                    ProfileStore.SaveCopy(first); ProfileStore.SaveCopy(first); ProfileStore.SaveCopy(second); ProfileStore.Load(second);
                    var sink = new Sink(); var r = Trial(Settings(mode: "observe"), sink: sink);
                    Expect(r.Status == "observed-no-input" && sink.Calls.Count == 0 && r.Inputs.Count == 10 && r.PreparationInputs.Count == 12,
                        "automatic observation submitted input or omitted releases");
                    Expect(r.Inputs.Concat(r.PreparationInputs).All(e => e.Action == "virtual-down" || e.Action == "virtual-up"), "observation event type");
                    var serializer = new JavaScriptSerializer(); var copy = serializer.Deserialize<NoticeRecord>(serializer.Serialize(r));
                    var p = ProfileStore.Snapshot(); p.Preparation[0].AtMs = 600; ProfileStore.Apply(p);
                    Expect(copy.ProfileSnapshot.Preparation[0].AtMs == 550 && copy.PreparationInputs.Count == 12 && copy.ActionIds.Length == 5,
                        "save/reload/snapshot lost or aliased preparation");
                }
                finally { ProfileStore.Load(original); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
            foreach (string phase in new[] { "preparation", "entry" })
            foreach (string reason in new[] { "F9", "focus", "geometry", "manual-direction", "manual-control" })
            {
                string selectedPhase = phase, selectedReason = reason;
                test("automatic cleanup " + selectedPhase + " / " + selectedReason, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        ProfileStore.Apply(AutomaticProfile());
                        var c = new Clock(); var sink = new Sink(); var probe = new Probe(); var held = new HashSet<string>();
                        double at = Double.PositiveInfinity;
                        sink.OnSend = (key, down) => { if (down) held.Add(key); else held.Remove(key); };
                        Func<bool> interrupted = () => c.Now >= at;
                        if (selectedReason == "focus") sink.Foreground = () => !interrupted();
                        if (selectedReason == "geometry") probe.Geometry = () => { if (interrupted()) throw new IOException("geometry"); };
                        if (selectedReason == "manual-direction") sink.DirectionExcept = key => interrupted();
                        if (selectedReason == "manual-control") sink.HeldExcept = key => interrupted();
                        var r = Trial(Settings(), c, sink, probe, cancel: () => selectedReason == "F9" && interrupted(),
                            save: record => { if (record.Status == "detected") at = record.Samples[record.FirstMatchIndex].CaptureEndMs + (selectedPhase == "preparation" ? 1450 : 3740); });
                        Expect(r.Status == (selectedReason == "F9" || selectedReason == "focus" ? "cancelled" : "failed") && held.Count == 0, "interruption left key held: " + r.Error);
                        Expect(new HashSet<string>(sink.Calls.Skip(sink.Calls.Count - 2)).SetEquals(new[] { "A-up", "RMB-up" }), "both held keys were not released");
                        Expect(r.Inputs.Count == (selectedPhase == "entry" ? 2 : 0), "continued main sequence after interruption");
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
            test("automatic preparation rejects manual direction at F8 and during confirmation", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(AutomaticProfile());
                    foreach (double at in new[] { 0.0, 220.0 })
                    {
                        var c = new Clock(); var sink = new Sink { DirectionExcept = key => c.Now >= at };
                        var r = Trial(Settings(), c, sink);
                        Expect(r.Status == "failed" && sink.Calls.Count == 0 && r.FirstMatchIndex == -1, "manual direction permitted before automatic preparation");
                    }
                }
                finally { ProfileStore.Apply(original); }
            });
            test("automatic preparation submission exceptions release every owned key despite release failure", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    ProfileStore.Apply(AutomaticProfile());
                    foreach (string scenario in new[] { "down-fail", "down-throw", "release-fail", "release-throw" })
                    {
                        var sink = new Sink();
                        sink.OnSend = (key, down) => {
                            if (key != "RMB" || !down) return;
                            if (scenario == "down-fail") { sink.FailKey = "RMB"; sink.FailDown = true; }
                            if (scenario == "down-throw") { sink.FailKey = "RMB"; sink.ThrowDown = true; }
                            if (scenario == "release-fail") { sink.FailKey = "A"; sink.FailUp = true; throw new IOException("down failed"); }
                            if (scenario == "release-throw") { sink.FailKey = "A"; sink.ThrowUp = true; throw new IOException("down failed"); }
                        };
                        var r = Trial(Settings(), sink: sink);
                        Expect(r.Status == (scenario.StartsWith("release") ? "release-failed" : "failed"), "submission failure status hidden");
                        Expect(new HashSet<string>(sink.Calls.Skip(sink.Calls.Count - 2)).SetEquals(new[] { "A-up", "RMB-up" }), "one failed release skipped another owned key");
                        Expect(r.Inputs.Count == 0, "submitted main after preparation failure");
                    }
                }
                finally { ProfileStore.Apply(original); }
            });
            test("late detection confirmation and late submission cancel automatic preparation without rebasing", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = AutomaticProfile(); p.Detector.ConfirmationMs = 700; ProfileStore.Apply(p);
                    var sink = new Sink(); var r = Trial(Settings(), sink: sink);
                    Expect(r.Status == "failed" && r.Error.Contains("50ms") && sink.Calls.Count == 0, "already overdue preparation was rebased");
                    ProfileStore.Apply(AutomaticProfile());
                    var c = new Clock(); sink = new Sink { OnSend = (key, down) => { if (key == "RMB" && down) c.Now += 250; } };
                    r = Trial(Settings(), c, sink);
                    Expect(r.Status == "failed" && r.Error.Contains("50ms") && r.Inputs.Count == 0 && sink.Calls.Last() == "RMB-up", "late preparation continued or omitted cleanup");
                }
                finally { ProfileStore.Apply(original); }
            });
        }
        private static void ManualPreparationTests(Action<string, Action> test)
        {
            test("manual preparation policy defaults to zero and rejects nonfinite or negative values", () => {
                var serializer = new JavaScriptSerializer();
                var fields = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(ProfileStore.Snapshot()));
                fields.Remove("ManualPreparationRmbUntilMs");
                Expect(ProfileStore.Parse(serializer.Serialize(fields)).ManualPreparationRmbUntilMs == 0, "legacy profile enabled manual preparation");
                foreach (double invalid in new[] { -1.0, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
                {
                    var p = ProfileStore.Snapshot(); p.ManualPreparationRmbUntilMs = invalid;
                    bool rejected = false; try { ProfileStore.Validate(p); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "invalid manual preparation policy accepted");
                }
            });
            test("preparation policy and declared conditions survive save, save-as, reload and trial snapshot", () => {
                string original = ProfileStore.SourcePath;
                string directory = Path.Combine(Path.GetTempPath(), "manual-preparation-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var p = ProfileStore.Snapshot(); p.ManualPreparationRmbUntilMs = 1000;
                    p.Conditions = "declared manual A/D preparation; not an observation";
                    p.Party = "A → B → C"; p.StartCharacter = "A";
                    ProfileStore.Apply(p);
                    string first = Path.Combine(directory, "profile.json"), second = Path.Combine(directory, "renamed.json");
                    ProfileStore.SaveCopy(first);
                    p = ProfileStore.Snapshot(); p.ManualPreparationRmbUntilMs = 1050; ProfileStore.Apply(p);
                    ProfileStore.SaveCopy(first); ProfileStore.Load(first);
                    Expect(ProfileStore.Current.ManualPreparationRmbUntilMs == 1050, "overwrite save lost policy");
                    ProfileStore.SaveCopy(second); ProfileStore.Load(second);
                    Expect(ProfileStore.Current.ManualPreparationRmbUntilMs == 1050 && ProfileStore.Current.Conditions == p.Conditions
                        && ProfileStore.Current.Party == p.Party && ProfileStore.Current.StartCharacter == p.StartCharacter, "save-as/reload lost declared conditions");
                    var record = Trial(Settings(mode: "observe"));
                    Expect(record.Status == "observed-no-input", "saved preparation profile did not execute in observation mode");
                    var serializer = new JavaScriptSerializer();
                    var copy = serializer.Deserialize<NoticeRecord>(serializer.Serialize(record));
                    p = ProfileStore.Snapshot(); p.ManualPreparationRmbUntilMs = 0; p.Conditions = "changed"; ProfileStore.Apply(p);
                    Expect(copy.ProfileSnapshot.ManualPreparationRmbUntilMs == 1050 && copy.ProfileSnapshot.Conditions != "changed"
                        && copy.Conditions == copy.ProfileSnapshot.Conditions && copy.Party == "A → B → C" && copy.StartCharacter == "A",
                        "trial snapshot changed with live profile");
                }
                finally { ProfileStore.Load(original); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
            test("only selected preparation cutoff collisions reject execution", () => {
                var original = ProfileStore.Snapshot();
                try
                {
                    var p = ProfileStore.Snapshot(); p.ManualPreparationRmbUntilMs = 1950;
                    p.Actions[0].Timing = new TimingCandidate { EarlyMs = 1900, BaselineMs = 1950, LateMs = 2000 };
                    p.Actions[0].SelectedTiming = "late"; ProfileStore.Apply(p);
                    Expect(ExperimentPlan.Resolve(ExperimentPlan.Defaults(), ExperimentPlan.PerActionPattern)[0] == 2000,
                        "unselected early/baseline cells prevented a valid late execution");
                    foreach (string pattern in new[] { "entry-early", "baseline" })
                    {
                        bool rejected = false;
                        try { ExperimentPlan.Resolve(ExperimentPlan.Defaults(), pattern); } catch (ArgumentException) { rejected = true; }
                        Expect(rejected, "selected first input at/before cutoff accepted");
                        var r = Settings(ExperimentPlan.PerActionPattern); r.Pattern = pattern;
                        r.DelaysMs[0] = pattern == "baseline" ? 1950 : 1900;
                        var sink = new Sink(); Trial(r, sink: sink);
                        Expect(r.Status == "failed" && r.Samples.Count == 0 && sink.Calls.Count == 0, "cutoff collision armed");
                    }
                }
                finally { ProfileStore.Apply(original); }
            });
            foreach (string scenario in new[] { "release-before-cutoff", "exact-cutoff", "held-across-cutoff", "before-detection", "during-confirmation",
                "Space", "auxiliary", "F9", "focus", "geometry", "before-automatic-input", "after-first-input", "observe", "legacy-zero" })
            {
                string name = scenario;
                test("manual preparation guards: " + name, () => {
                    var original = ProfileStore.Snapshot();
                    try
                    {
                        var p = ProfileStore.Snapshot(); p.ManualPreparationRmbUntilMs = name == "legacy-zero" ? 0 : 1000;
                        ProfileStore.Apply(p);
                        // A nonzero clock start detects accidental mixing of absolute and notice-relative clocks.
                        const double clockStart = 1234;
                        var c = new Clock { Now = clockStart }; var sink = new Sink(); var probe = new Probe();
                        double noticeAbsolute = Double.PositiveInfinity, cutoff = Double.PositiveInfinity;
                        bool detected = false, sawManualRmb = false;
                        Func<bool> physicalRmb = () => {
                            bool active;
                            if (name == "before-detection") active = c.Now >= clockStart + 100;
                            else if (name == "during-confirmation") active = c.Now >= clockStart + 220;
                            else if (!detected) active = false;
                            else if (name == "exact-cutoff") active = c.Now >= cutoff;
                            else if (name == "held-across-cutoff") active = c.Now >= cutoff - 10 && c.Now < cutoff + 10;
                            else if (name == "before-automatic-input") active = c.Now >= noticeAbsolute + 2000;
                            else if (name == "after-first-input") active = c.Now >= noticeAbsolute + 2100;
                            else active = c.Now >= noticeAbsolute + 100 && c.Now < cutoff - 1;
                            if (active) sawManualRmb = true;
                            return active;
                        };
                        sink.HeldExcept = ignored => {
                            bool rmb = physicalRmb();
                            bool space = name == "Space" && detected && c.Now >= noticeAbsolute + 200;
                            bool auxiliary = name == "auxiliary" && detected && c.Now >= noticeAbsolute + 200;
                            return (rmb && ignored != "RMB") || (space && ignored != "Space") || auxiliary;
                        };
                        Func<bool> interrupted = () => detected && c.Now >= noticeAbsolute + 200;
                        if (name == "focus") sink.Foreground = () => !interrupted();
                        if (name == "geometry") probe.Geometry = () => { if (interrupted()) throw new InvalidOperationException("geometry changed"); };
                        var r = Trial(Settings(mode: name == "observe" ? "observe" : ExperimentPlan.LiveMode), c, sink, probe,
                            cancel: () => name == "F9" && interrupted(), save: record => {
                                if (record.Status != "detected") return;
                                detected = true;
                                noticeAbsolute = clockStart + record.Samples[record.FirstMatchIndex].CaptureEndMs;
                                cutoff = noticeAbsolute + 1000;
                            });
                        Expect(sawManualRmb, "test never exercised a physical manual RMB");
                        if (name == "release-before-cutoff")
                        {
                            Expect(r.Status == "submitted-not-game-verified" && sink.Calls.Count == ExperimentPlan.Count * 2,
                                "released preparation RMB blocked schedule or generated extra native calls");
                            Expect(r.Inputs[0].BeginMs == r.DueMs[0], "manual preparation changed first deadline");
                            Expect(sink.Calls.SequenceEqual(ProfileStore.Current.Actions.SelectMany(a => new[] { a.Key + "-down", a.Key + "-up" })),
                                "manual RMB became a program-owned press/release");
                        }
                        else if (name == "observe")
                            Expect(r.Status == "observed-no-input" && sink.Calls.Count == 0 && r.Inputs.All(i => i.Action == "virtual-down")
                                && r.Inputs.Count == ExperimentPlan.Count, "observation mode sent or released manual input");
                        else if (name == "after-first-input")
                            Expect(r.Status == "failed" && sink.Calls.SequenceEqual(new[] { "RMB-down", "RMB-up" }), "manual exception survived first input or released unowned key");
                        else
                        {
                            string expected = name == "F9" || name == "focus" ? "cancelled" : "failed";
                            Expect(r.Status == expected && sink.Calls.Count == 0 && r.Inputs.Count == 0,
                                "guard did not stop before automatic input, or cleanup released a manual key: " + r.Status + " / " + r.Error);
                            if (name == "exact-cutoff" || name == "held-across-cutoff")
                                Expect(c.Now == cutoff, "preparation cutoff was frozen until the next outer wait check");
                            if (name == "before-detection" || name == "during-confirmation")
                                Expect(r.FirstMatchIndex == -1, "manual preparation was enabled before confirmation");
                        }
                    }
                    finally { ProfileStore.Apply(original); }
                });
            }
        }
        public static int Replay(string input, string output, int firstLow, int firstHigh, int lastHigh)
        {
            int size = NoticeMatcher.Width * NoticeMatcher.Height * 3;
            byte[] data = File.ReadAllBytes(input);
            if (data.Length % size != 0) throw new InvalidDataException("Expected packed RGB24 ROI frames");
            var matcher = new NoticeMatcher(); var samples = new List<NoticeSample>(); var matches = new List<int>();
            var edge = new NoticeEdge(); int first = -1, confirmed = -1;
            using (var bitmap = new Bitmap(NoticeMatcher.Width, NoticeMatcher.Height, PixelFormat.Format24bppRgb))
            for (int f = 0; f < data.Length / size; f++)
            {
                var bgr = new byte[size]; for (int i = 0; i < size; i += 3) { bgr[i] = data[f * size + i + 2]; bgr[i + 1] = data[f * size + i + 1]; bgr[i + 2] = data[f * size + i]; }
                BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try { Marshal.Copy(bgr, 0, locked.Scan0, size); } finally { bitmap.UnlockBits(locked); }
                double score = matcher.Score(bitmap); var sample = new NoticeSample { CaptureBeginMs = f * 1000.0 / 60, CaptureEndMs = f * 1000.0 / 60, AnalysisEndMs = f * 1000.0 / 60, Score = score, Present = score >= NoticeMatcher.Threshold };
                samples.Add(sample); if (sample.Present) matches.Add(f);
                if (confirmed < 0 && edge.Push(sample, f, samples)) { first = edge.First; confirmed = f; }
            }
            bool pass = first >= firstLow && first <= firstHigh && matches.All(f => f >= firstLow && f <= lastHigh);
            File.WriteAllText(output, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { frames = samples.Count, first = first, confirmed = confirmed, matched_frames = matches, samples = samples,
                expected_first_low = firstLow, expected_first_high = firstHigh, expected_last_high = lastHigh, passed = pass, native_input_calls = 0 }));
            return pass ? 0 : 1;
        }

        public static int CaptureTest(string output)
        {
            // Only a synthetic test window is captured. No GameInputSink or SendInput.
            var clock = new StopwatchClock(); var samples = new List<NoticeSample>();
            using (var form = new Form { ClientSize = new Size(ProfileStore.Current.Detector.ReferenceWidth, ProfileStore.Current.Detector.ReferenceHeight), BackColor = Color.Black,
                StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Text = "Vesper synthetic capture test" })
            using (var image = new Bitmap(ProfileStore.TemplatePath))
            {
                form.Show(); form.Hide(); form.Show(); form.TopMost = true; Application.DoEvents(); System.Threading.Thread.Sleep(200);
                using (var probe = new GameNoticeProbe(form.Handle))
                {
                    long origin = clock.Timestamp;
                    var absent = probe.Capture(clock, origin); Expect(!absent.Present, "blank window matched");
                    var picture = new PictureBox { Image = image, Location = new Point(ProfileStore.Current.Detector.X, ProfileStore.Current.Detector.Y), Size = image.Size };
                    form.Controls.Add(picture); form.BringToFront(); form.Activate(); form.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(200);
                    for (int i = 0; i < 30; i++)
                    {
                        var sample = probe.Capture(clock, origin); samples.Add(sample);
                        if (!sample.Present) probe.SaveDetectedImage(Path.ChangeExtension(output, ".failed.png"));
                        Expect(sample.Present, "synthetic notice not captured"); System.Threading.Thread.Sleep(8);
                    }
                    form.Left += 10;
                    bool rejected = false; try { probe.CheckGeometry(); } catch (InvalidOperationException) { rejected = true; }
                    Expect(rejected, "moved window accepted");
                }
                form.Close();
            }
            var record = new NoticeRecord { Mode = "synthetic-capture-no-input", Status = "passed", Samples = samples };
            record.Summarize(); NoticeRecord.Save(output, record); return 0;
        }
    }
}
