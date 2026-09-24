using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VesperLab
{
    public sealed class NoticeRecord
    {
        public string TrialId = Guid.NewGuid().ToString("N"), BossId = ProfileStore.Current.BossId, ProfileId = ProfileStore.Current.Id, ProfileHash = ProfileStore.Hash, TemplateHash = ProfileStore.TemplateHash;
        public ExperimentProfile ProfileSnapshot = ProfileStore.Snapshot();
        public string SourceProfilePath = ProfileStore.SourcePath, SourceProfileHash = ProfileStore.SourceHash;
        public string Party = ProfileStore.Current.Party, StartCharacter = ProfileStore.Current.StartCharacter;
        public string EndedUtc, ObsLedgerDirectory;
        public string F8Utc, RecordingBasis;
        public ObsObservation RecordingAtF8;
        public ObsObservation[] ObsObservations = new ObsObservation[0];
        public string[] ActionIds = ProfileStore.Current.Actions.Select(a => a.Id).ToArray(), ActionKeys = ProfileStore.Current.Actions.Select(a => a.Key).ToArray();
        public string Version = ExperimentPlan.Version, Skill = ExperimentPlan.Skill, Mode, StartedUtc = DateTime.UtcNow.ToString("o"), Status = "armed", Error = "";
        public string Detector = ExperimentPlan.Detector, TimingOrigin = "first matching capture end; not first visible game frame";
        public string GameOutcome = "미확인", Conditions = ExperimentPlan.Conditions;
        public double Threshold = NoticeMatcher.Threshold, PollMs = ProfileStore.Current.Detector.PollMs, ConfirmationMs = ProfileStore.Current.Detector.ConfirmationMs, TimeoutMs = ProfileStore.Current.Detector.TimeoutMs;
        public long OriginQpc, QpcFrequency;
        public int ProcessId, ClientWidth, ClientHeight;
        public int PreviousAbsentIndex = -1, FirstMatchIndex = -1, ConfirmedIndex = -1;
        public string Pattern = "baseline";
        public string[] SelectedTimings;
        public TimingCandidate[] Candidates = ExperimentPlan.Defaults();
        public double[] DelaysMs = new double[0];
        public double[] DueMs;
        public string Recording = "unspecified";
        public string DetectedImage = "";
        public List<NoticeSample> Samples = new List<NoticeSample>();
        public List<InputEvent> Inputs = new List<InputEvent>();
        public List<InputEvent> PreparationInputs = new List<InputEvent>();
        public List<InputEvent> AuxiliaryInputs = new List<InputEvent>();
        public List<InputEvent> StartAttackInputs = new List<InputEvent>();
        public List<InputEvent> StartInputs = new List<InputEvent>();
        public string StartTimingOrigin = "F8 monitoring loop start; not physical F8 key-down";
        public double CaptureP50Ms, CaptureP95Ms, CaptureMaxMs, AnalysisP95Ms, PollGapMaxMs;
        public void FreezeRecordingAtF8(DateTime atUtc, ObsObservation observation)
        {
            F8Utc = atUtc.ToUniversalTime().ToString("o");
            RecordingBasis = "f8-snapshot-unknown-off";
            RecordingAtF8 = observation == null ? new ObsObservation { AtUtc = atUtc, State = "UNKNOWN", Detail = "OBS not connected" } : observation.Copy();
            if (atUtc - RecordingAtF8.AtUtc > TimeSpan.FromSeconds(3))
            {
                RecordingAtF8.State = "UNKNOWN";
                RecordingAtF8.Detail = "OBS status has not been refreshed for over 3 seconds";
            }
            // User policy: disconnected/unknown and paused recording count as OFF.
            Recording = RecordingAtF8.State == "ON" ? "ON" : "OFF";
        }
        public static void Save(string path, NoticeRecord record)
        {
            if (File.Exists(path))
            {
                var existing = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<NoticeRecord>(File.ReadAllText(path));
                if (existing.TrialId != record.TrialId || (existing.Status != "armed" && existing.Status != "detected"))
                    throw new IOException("완료된 원시 회차 기록은 덮어쓸 수 없습니다. 정정은 별도 결과 기록에 남기세요.");
            }
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(record));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public void Summarize()
        {
            double[] capture = Samples.Select(s => s.CaptureEndMs - s.CaptureBeginMs).OrderBy(v => v).ToArray();
            double[] analyze = Samples.Select(s => s.AnalysisEndMs - s.CaptureEndMs).OrderBy(v => v).ToArray();
            if (capture.Length == 0) return;
            CaptureP50Ms = capture[(int)((capture.Length - 1) * .5)]; CaptureP95Ms = capture[(int)((capture.Length - 1) * .95)];
            CaptureMaxMs = capture[capture.Length - 1]; AnalysisP95Ms = analyze[(int)((analyze.Length - 1) * .95)];
            for (int i = 1; i < Samples.Count; i++) PollGapMaxMs = Math.Max(PollGapMaxMs, Samples[i].CaptureBeginMs - Samples[i - 1].CaptureBeginMs);
        }
    }

    public static class NoticeRunner
    {
        private sealed class ScheduledEvent
        {
            public string Key;
            public int Attack;
            public bool Preparation, Auxiliary, StartAttack, StartInput, Down;
            public double Due, HoldMs;
        }
        public const double HoldMs = ExperimentPlan.HoldMs;
        public static void Validate(NoticeRecord r)
        {
            if (r.ProfileId != ProfileStore.Current.Id || r.ProfileHash != ProfileStore.Hash || r.BossId != ProfileStore.Current.BossId || r.ActionIds == null || !r.ActionIds.SequenceEqual(ProfileStore.Current.Actions.Select(a => a.Id)) || r.ActionKeys == null || !r.ActionKeys.SequenceEqual(ProfileStore.Current.Actions.Select(a => a.Key)))
                throw new ArgumentException("회차 프로필·대응 식별자가 실행 설정과 다릅니다.");
            if (r.Skill != ExperimentPlan.Skill || (r.Mode != "observe" && r.Mode != ExperimentPlan.LiveMode))
                throw new ArgumentException(ExperimentPlan.Skill + "의 관찰 또는 전체 입력 모드를 선택하세요.");
            string[] selected = ExperimentPlan.SelectedTimings(r.Pattern);
            if (r.SelectedTimings != null && !r.SelectedTimings.SequenceEqual(selected))
                throw new ArgumentException("회차의 셀 선택과 실행 설정이 다릅니다.");
            double[] planned = ExperimentPlan.Resolve(r.Candidates, r.Pattern);
            if (r.DelaysMs == null || !planned.SequenceEqual(r.DelaysMs))
                throw new ArgumentException("조합과 실제 입력 시각이 일치하지 않습니다.");
            if (!new[] { "ON", "OFF", "MIXED", "UNKNOWN" }.Contains(r.Recording)) throw new ArgumentException("녹화 관측 상태가 잘못됐습니다.");
            r.SelectedTimings = selected;
        }
        public static NoticeRecord Run(NoticeRecord r, ITrialClock clock, IInputSink sink, INoticeProbe probe,
            Func<bool> cancel, Action<NoticeRecord> save, Action<string> progress)
        {
            string held = null; int heldAttack = 0; long origin = clock.Timestamp; r.StartedUtc = DateTime.UtcNow.ToString("o"); r.OriginQpc = origin; r.QpcFrequency = clock.Frequency;
            var automaticHeld = new Dictionary<string, ScheduledEvent>();
            var startAttack = ProfileStore.Current.StartAttack;
            var startInputs = ProfileStore.Current.StartInputs ?? new StartInputPress[0];
            var startEvents = new List<ScheduledEvent>();
            if (startAttack != null)
                for (int i = 0; i < startAttack.AtMs.Length; i++)
                    startEvents.Add(new ScheduledEvent { Key = startAttack.Key, Attack = i, StartAttack = true, Down = true,
                        Due = startAttack.AtMs[i], HoldMs = startAttack.HoldMs });
            else
                for (int i = 0; i < startInputs.Length; i++)
                    startEvents.Add(new ScheduledEvent { Key = startInputs[i].Key, Attack = i, StartInput = true, Down = true,
                        Due = startInputs[i].AtMs, HoldMs = startInputs[i].HoldMs });
            var startHeld = new Dictionary<string, ScheduledEvent>();
            double lastStartReleaseEndMs = -1;
            bool automaticPreparation = ProfileStore.Current.Preparation != null && ProfileStore.Current.Preparation.Length > 0;
            bool automaticAuxiliary = ProfileStore.Current.AuxiliaryInputs != null && ProfileStore.Current.AuxiliaryInputs.Any(a => a.Enabled);
            bool automaticSchedule = automaticPreparation || automaticAuxiliary;
            bool guardLmb = startAttack != null || (automaticAuxiliary && ProfileStore.Current.AuxiliaryInputs.Any(a => a.Enabled && a.Key == "LMB"));
            bool guardE = startAttack != null && startAttack.Key == "E";
            bool allowManualMovement = ProfileStore.Current.AllowManualMovement;
            bool guardDirections = !allowManualMovement && (automaticPreparation || automaticAuxiliary || startAttack != null || startInputs.Length > 0);
            double noticeOrigin = -1, preparationUntil = ProfileStore.Current.ManualPreparationRmbUntilMs;
            bool firstInputPending = true;
            // Permission is checked at the current time, never latched across a wait.
            // Ignoring a user's RMB here does not transfer ownership to this runner.
            Func<string> preparationKey = () => firstInputPending && noticeOrigin >= 0 && preparationUntil > 0
                && clock.ElapsedMs(origin) < noticeOrigin + preparationUntil ? "RMB" : null;
            Func<bool> stop = () => cancel() || !sink.IsTargetForeground();
            Func<bool> waitGuard = () => {
                if (stop()) return true;
                if (startEvents.Count > 0) probe.CheckGeometry();
                if (sink.AnyControlHeld(held ?? preparationKey(), guardLmb, guardE) || (!allowManualMovement && (startAttack != null || startInputs.Length > 0) && sink.AnyDirectionHeld())) throw new InvalidOperationException("대기 중 허용되지 않은 수동 대응 입력을 감지했습니다.");
                return false;
            };
            Action check = () => {
                if (stop()) throw new OperationCanceledException("F9 중단 또는 게임 창 이탈");
                probe.CheckGeometry();
            };
            Action detectionGuard = () => {
                check();
                string ownedControl = r.Mode == "observe" ? null : startHeld.Keys.FirstOrDefault(IsControl);
                string ownedDirection = r.Mode == "observe" ? null : startHeld.Keys.FirstOrDefault(k => k == "W");
                if (sink.AnyControlHeld(ownedControl, guardLmb, guardE)
                    || (guardDirections && sink.AnyDirectionHeld(ownedDirection)))
                    throw new InvalidOperationException("감시 중 수동 대응·시작 공격·방향 입력을 감지하여 중단했습니다.");
            };
            try
            {
                Validate(r);
                check(); if (sink.AnyControlHeld(null, guardLmb, guardE) || (guardDirections && sink.AnyDirectionHeld())) throw new InvalidOperationException(allowManualMovement ? "자동 대응 키를 놓고 F8을 누르세요. WASD 이동은 허용됩니다." : "대응 키와 자동 시작 키·WASD를 놓고 F8을 누르세요.");
                save(r); if (progress != null) progress("감시 중 · 문구가 없는 상태에서 새 출현을 기다립니다.");
                var edge = new NoticeEdge();
                while (clock.ElapsedMs(origin) < r.TimeoutMs)
                {
                    detectionGuard();
                    if (startEvents.Count > 0)
                    {
                        var nextStart = startEvents.OrderBy(e => e.Due).ThenBy(e => e.Down ? 1 : 0).First();
                        if (clock.ElapsedMs(origin) >= nextStart.Due)
                        {
                            if (clock.ElapsedMs(origin) - nextStart.Due > 50) throw new InvalidOperationException("시작 입력 예약보다 50ms 이상 늦어 중단했습니다.");
                            startEvents.Remove(nextStart);
                            if (nextStart.Down)
                            {
                                if (startHeld.ContainsKey(nextStart.Key)) throw new InvalidOperationException("시작 입력 유지 시간이 겹칩니다.");
                                startHeld.Add(nextStart.Key, nextStart); // Own before Send, including a failed submission.
                                SubmitStartEvent(r, sink, clock, origin, nextStart);
                                startEvents.Add(new ScheduledEvent { Key = nextStart.Key, Attack = nextStart.Attack,
                                    StartAttack = nextStart.StartAttack, StartInput = nextStart.StartInput, Down = false,
                                    Due = clock.ElapsedMs(origin) + nextStart.HoldMs });
                            }
                            else
                            {
                                SubmitStartEvent(r, sink, clock, origin, nextStart);
                                lastStartReleaseEndMs = clock.ElapsedMs(origin);
                                startHeld.Remove(nextStart.Key);
                            }
                            if (startEvents.Any(e => e.Down && startHeld.ContainsKey(e.Key) && e.Due < startEvents.Where(x => !x.Down && x.Key == e.Key).Select(x => x.Due).DefaultIfEmpty(Double.PositiveInfinity).Min()))
                                throw new InvalidOperationException("시작 입력 전송 지연으로 다음 유지 시간이 겹칩니다.");
                            detectionGuard();
                        }
                    }
                    NoticeSample sample = probe.Capture(clock, origin); r.Samples.Add(sample); check();
                    if (sample.AnalysisEndMs - sample.CaptureBeginMs > 100)
                        throw new InvalidOperationException("캡처·분석이 100ms를 초과했습니다. 기록을 확인하세요.");
                    if (clock.ElapsedMs(origin) >= r.TimeoutMs) break;
                    bool confirmed = edge.Push(sample, r.Samples.Count - 1, r.Samples);
                    if (startInputs.Length > 0 && edge.First >= 0 && startEvents.Count > 0)
                    {
                        r.PreviousAbsentIndex = edge.PreviousAbsent; r.FirstMatchIndex = edge.First;
                        throw new InvalidOperationException("시작 W/RMB 입력의 해제 완료 전에 문구가 처음 일치하여 중단했습니다.");
                    }
                    if (confirmed)
                    {
                        r.PreviousAbsentIndex = edge.PreviousAbsent; r.FirstMatchIndex = edge.First; r.ConfirmedIndex = edge.Confirmed;
                        noticeOrigin = r.Samples[edge.First].CaptureEndMs;
                        if ((startAttack != null || startInputs.Length > 0) && (startEvents.Count > 0 || noticeOrigin < lastStartReleaseEndMs))
                            throw new InvalidOperationException("시작 " + (startAttack != null ? startAttack.Key + " "
                                + (startAttack.Key == "E" ? "한 번" : "다섯 번") : "W/RMB 입력") + "의 해제 완료 전에 문구가 감지되어 중단했습니다.");
                        r.DueMs = r.DelaysMs.Select(delay => noticeOrigin + delay).ToArray();
                        r.Status = "detected"; save(r);
                        if (progress != null) progress((r.Mode == "observe" ? "문구 감지 · 가상 입력 시각까지 대기 중" : "문구 감지 · 입력 예약됨")
                            + (preparationUntil > 0 ? " · 감지 기준 " + preparationUntil + "ms 미만 준비 RMB 허용 / F9 상시 중단" : ""));
                        break;
                    }
                    // Avoid busy-spinning for the entire detection phase.
                    System.Threading.Thread.Sleep(1);
                    double next = sample.CaptureBeginMs + r.PollMs;
                    if (startEvents.Count > 0) next = Math.Min(next, startEvents.Min(e => e.Due));
                    clock.WaitUntil(origin, next, startAttack == null && startInputs.Length == 0 ? stop : (Func<bool>)(() => { detectionGuard(); return false; }));
                }
                if (r.DueMs == null) throw new TimeoutException("설정한 대기 시간 안에 새 문구를 감지하지 못했습니다. 문구 출현 전에 F8을 누르세요.");
                if (automaticSchedule)
                    RunAutomaticSchedule(r, clock, sink, origin, noticeOrigin, automaticHeld, check, guardLmb, guardE);
                else
                {
                var schedule = r.DueMs.Select((due, i) => new ScheduledPress { Attack = i, Key = r.ActionKeys[i], AtMs = due });
                // All deadlines share the notice origin, independent of earlier input or API duration.
                foreach (ScheduledPress press in schedule)
                {
                    double due = press.AtMs.Value;
                    while (clock.ElapsedMs(origin) < due)
                    {
                        check(); if (sink.AnyControlHeld(preparationKey(), guardLmb, guardE) || (!allowManualMovement && (startAttack != null || startInputs.Length > 0) && sink.AnyDirectionHeld())) throw new InvalidOperationException("예약 대기 중 허용되지 않은 수동 대응 입력을 감지했습니다.");
                        clock.WaitUntil(origin, Math.Min(due, clock.ElapsedMs(origin) + 100), waitGuard);
                    }
                    firstInputPending = false;
                    check(); if (sink.AnyControlHeld(null, guardLmb, guardE) || (!allowManualMovement && (startAttack != null || startInputs.Length > 0) && sink.AnyDirectionHeld())) throw new InvalidOperationException("입력 직전에 수동 입력이 감지됐습니다.");
                    if (clock.ElapsedMs(origin) - due > 50) throw new InvalidOperationException("예약 시각보다 50ms 이상 늦어 입력을 취소했습니다.");
                    if (r.Mode == "observe")
                    {
                        r.Inputs.Add(new InputEvent { Action = "virtual-down", Key = press.Key, Attack = press.Attack,
                            DueMs = due, BeginMs = clock.ElapsedMs(origin), EndMs = clock.ElapsedMs(origin) });
                    }
                    else
                    {
                        // Ownership before Send guarantees a release attempt even if Send throws.
                        held = press.Key; heldAttack = press.Attack;
                        Send(r, sink, clock, origin, held, heldAttack, true, due);
                        double release = clock.ElapsedMs(origin) + ProfileStore.Current.Actions[press.Attack].HoldMs;
                        clock.WaitUntil(origin, release, waitGuard);
                        Send(r, sink, clock, origin, held, heldAttack, false, release); held = null;
                        check();
                    }
                }
                }
                r.Status = r.Mode == "observe" ? "observed-no-input" : "submitted-not-game-verified";
            }
            catch (OperationCanceledException e) { r.Status = "cancelled"; r.Error = e.Message; }
            catch (Exception e) { r.Status = "failed"; r.Error = e.Message; }
            finally
            {
                foreach (var press in startHeld.Values.ToArray())
                {
                    try { SubmitStartEvent(r, sink, clock, origin, new ScheduledEvent { Key = press.Key, Attack = press.Attack,
                        StartAttack = press.StartAttack, StartInput = press.StartInput, Down = false, Due = clock.ElapsedMs(origin) }); }
                    catch (Exception e) { r.Status = "release-failed"; r.Error += " / " + press.Key + " 해제 실패: " + e.Message; }
                }
                // Each owned key gets its own release attempt even when another release fails.
                foreach (var press in automaticHeld.Values.ToArray())
                {
                    try { SubmitEvent(r, sink, clock, origin, new ScheduledEvent { Key = press.Key, Attack = press.Attack, Preparation = press.Preparation, Auxiliary = press.Auxiliary, Down = false, Due = clock.ElapsedMs(origin) }); }
                    catch (Exception e) { r.Status = "release-failed"; r.Error += " / " + press.Key + " 해제 실패: " + e.Message; }
                }
                if (held != null)
                {
                    try { Send(r, sink, clock, origin, held, heldAttack, false, clock.ElapsedMs(origin)); }
                    catch (Exception e) { r.Status = "release-failed"; r.Error += " / " + held + " 해제 실패: " + e.Message; }
                }
                // Disk writes and PNG compression are outside the timed input path.
                if (r.FirstMatchIndex >= 0 && !String.IsNullOrEmpty(r.DetectedImage))
                    try { probe.SaveDetectedImage(r.DetectedImage); } catch (Exception e) { r.Error += " / 감지 이미지 저장 실패: " + e.Message; }
                r.EndedUtc = DateTime.UtcNow.ToString("o"); r.Summarize(); save(r);
            }
            return r;
        }
        private static bool IsControl(string key) { return key == "LMB" || key == "RMB" || key == "Space"; }
        private static void RunAutomaticSchedule(NoticeRecord r, ITrialClock clock, IInputSink sink, long origin,
            double noticeOrigin, Dictionary<string, ScheduledEvent> owned, Action check, bool guardLmb, bool guardE)
        {
            var events = new List<ScheduledEvent>();
            var preparation = ProfileStore.Current.Preparation ?? new PreparationPress[0];
            for (int i = 0; i < preparation.Length; i++)
            {
                var p = preparation[i];
                events.Add(new ScheduledEvent { Key = p.Key, Attack = i, Preparation = true, Down = true, Due = noticeOrigin + p.AtMs });
                events.Add(new ScheduledEvent { Key = p.Key, Attack = i, Preparation = true, Down = false, Due = noticeOrigin + p.AtMs + p.HoldMs });
            }
            var auxiliary = ProfileStore.Current.AuxiliaryInputs ?? new AuxiliaryPress[0];
            for (int i = 0; i < auxiliary.Length; i++)
            {
                var p = auxiliary[i];
                if (!p.Enabled) continue;
                events.Add(new ScheduledEvent { Key = p.Key, Attack = i, Auxiliary = true, Down = true, Due = noticeOrigin + p.AtMs });
            }
            for (int i = 0; i < r.DueMs.Length; i++)
            {
                events.Add(new ScheduledEvent { Key = r.ActionKeys[i], Attack = i, Down = true, Due = r.DueMs[i] });
            }
            Action guard = () => {
                check();
                string control = r.Mode == "observe" ? null : owned.Keys.FirstOrDefault(IsControl);
                string direction = r.Mode == "observe" ? null : owned.Keys.FirstOrDefault(k => k == "A" || k == "D");
                if (sink.AnyControlHeld(control, guardLmb, guardE) || (!ProfileStore.Current.AllowManualMovement && sink.AnyDirectionHeld(direction)))
                    throw new InvalidOperationException("자동 준비 중 수동 대응·방향 입력을 감지했습니다.");
            };
            // Down and preparation release deadlines share the notice origin. Main holds keep
            // each action's duration after Send returns; insert releases into the same queue.
            while (events.Count > 0)
            {
                var e = events.OrderBy(item => item.Due).ThenBy(item => item.Down ? 1 : 0).First();
                if (e.Down && IsControl(e.Key) && owned.Keys.Any(IsControl))
                {
                    var release = events.Where(item => !item.Down && IsControl(item.Key) && owned.ContainsKey(item.Key)).OrderBy(item => item.Due).FirstOrDefault();
                    if (release != null) e = release;
                }
                events.Remove(e);
                guard();
                clock.WaitUntil(origin, e.Due, () => { guard(); return false; });
                guard();
                if (clock.ElapsedMs(origin) - e.Due > 50)
                    throw new InvalidOperationException("예약 시각보다 50ms 이상 늦어 입력을 취소했습니다.");
                if (e.Down && IsControl(e.Key) && owned.Keys.Any(IsControl))
                    throw new InvalidOperationException("앞 대응의 키 유지가 끝나지 않아 다음 입력을 취소했습니다.");
                if (e.Down) owned.Add(e.Key, e); // Acquire before Send, including a throwing submission.
                SubmitEvent(r, sink, clock, origin, e);
                if (e.Down && e.Auxiliary)
                    events.Add(new ScheduledEvent { Key = e.Key, Attack = e.Attack, Auxiliary = true, Down = false, Due = clock.ElapsedMs(origin) + auxiliary[e.Attack].HoldMs });
                else if (e.Down && !e.Preparation)
                    events.Add(new ScheduledEvent { Key = e.Key, Attack = e.Attack, Down = false, Due = clock.ElapsedMs(origin) + ProfileStore.Current.Actions[e.Attack].HoldMs });
                if (!e.Down) owned.Remove(e.Key);
                guard();
            }
        }
        private static void SubmitStartEvent(NoticeRecord r, IInputSink sink, ITrialClock clock, long origin, ScheduledEvent start)
        {
            var target = start.StartAttack ? r.StartAttackInputs : r.StartInputs;
            if (r.Mode == "observe")
                target.Add(new InputEvent { Action = start.Down ? "virtual-down" : "virtual-up", Key = start.Key, Attack = start.Attack,
                    DueMs = start.Due, BeginMs = clock.ElapsedMs(origin), EndMs = clock.ElapsedMs(origin) });
            else SendTo(target, sink, clock, origin, start.Key, start.Attack, start.Down, start.Due);
        }
        private static void SubmitEvent(NoticeRecord r, IInputSink sink, ITrialClock clock, long origin, ScheduledEvent scheduled)
        {
            var target = scheduled.Preparation ? r.PreparationInputs : scheduled.Auxiliary ? r.AuxiliaryInputs : r.Inputs;
            if (r.Mode == "observe")
            {
                target.Add(new InputEvent { Action = scheduled.Down ? "virtual-down" : "virtual-up", Key = scheduled.Key,
                    Attack = scheduled.Attack, DueMs = scheduled.Due, BeginMs = clock.ElapsedMs(origin), EndMs = clock.ElapsedMs(origin) });
            }
            else SendTo(target, sink, clock, origin, scheduled.Key, scheduled.Attack, scheduled.Down, scheduled.Due);
        }
        private static void Send(NoticeRecord r, IInputSink sink, ITrialClock clock, long origin, string key, int attack, bool down, double due)
        {
            SendTo(r.Inputs, sink, clock, origin, key, attack, down, due);
        }
        private static void SendTo(List<InputEvent> target, IInputSink sink, ITrialClock clock, long origin, string key, int attack, bool down, double due)
        {
            var e = new InputEvent { Action = down ? "down" : "up", Key = key, Attack = attack, DueMs = due, BeginMs = clock.ElapsedMs(origin) };
            target.Add(e); int error;
            try { e.Inserted = sink.Send(key, down, out error); e.Win32Error = error; }
            finally { e.EndMs = clock.ElapsedMs(origin); }
            if (e.Inserted != 1) throw new InvalidOperationException("OS 입력 제출 실패: " + error);
        }
    }
}
