using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace VesperLab
{
    public sealed class LaneInput
    {
        public string Key, ActionId, NearestActionId;
        public long HookQpc;
        public double TimeMs, HookToUiMs, OsEventToHookMs, NearestLateDifferenceMs;
        public bool InsideWindow, Accepted;
    }
    public sealed class LaneRun
    {
        public string Version = "1.0.0", Id = Guid.NewGuid().ToString("N"), Mode = "manual-lane-no-injection";
        public string StartedUtc = DateTime.UtcNow.ToString("o"), EndedUtc, Status = "armed", Error, GameOutcome;
        public string TimingOrigin = "first matching capture end; same as experiment, no recording offset";
        public string InputTimeBasis = "passive low-level hook QPC, not physical switch or game receipt";
        public string PaintTimeBasis = "application paint begin/end, not compositor or monitor presentation";
        public ExperimentProfile Profile;
        public string SourceProfileHash, ProfileHash, TemplateHash;
        public int ClientX, ClientY, ClientWidth, ClientHeight, DetectorX, DetectorY, DetectorWidth, DetectorHeight;
        public long ArmedQpc, Frequency = Stopwatch.Frequency;
        public double? NoticeOriginMs;
        public int FirstMatchIndex = -1, ConfirmedIndex = -1;
        public List<NoticeSample> Samples = new List<NoticeSample>();
        public List<LaneInput> Inputs = new List<LaneInput>();
        public List<LanePaint> Paints = new List<LanePaint>();
        public bool[] LaneAccepted;
        public double TimeAt(long qpc) { return (qpc - ArmedQpc) * 1000.0 / Frequency - NoticeOriginMs.Value; }
        public void AddInput(string key, long qpc, double osDelay, long uiQpc)
        {
            double time = TimeAt(qpc);
            var input = new LaneInput { Key = key, HookQpc = qpc, TimeMs = time, OsEventToHookMs = osDelay, HookToUiMs = (uiQpc - qpc) * 1000.0 / Frequency };
            int nearest = 0; double distance = Double.MaxValue;
            for (int i = 0; i < Profile.Actions.Length; i++)
            {
                var a = Profile.Actions[i]; var t = a.Timing;
                double d = time < t.EarlyMs ? t.EarlyMs - time : time > t.LateMs ? time - t.LateMs : 0;
                if (d < distance) { distance = d; nearest = i; }
                if (!input.InsideWindow && a.Key == key && time >= t.EarlyMs && time <= t.LateMs)
                { input.InsideWindow = true; input.ActionId = a.Id; input.Accepted = !LaneAccepted[i]; LaneAccepted[i] = true; }
            }
            input.NearestActionId = Profile.Actions[nearest].Id;
            input.NearestLateDifferenceMs = time - Profile.Actions[nearest].Timing.LateMs;
            Inputs.Add(input);
        }
        public static bool ValidOutcome(string value, int count)
        { return value != null && value.Length == count && value.All(c => c == '0' || c == '1'); }
        public string Save(string directory, string bits)
        {
            if (Status != "completed" || !ValidOutcome(bits, Profile.Actions.Length)) throw new InvalidOperationException("완료 회차에 동작 수만큼 0/1을 입력하세요.");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Profile.Id + "-" + Id.Substring(0,8) + ".json");
            if (File.Exists(path)) throw new IOException("같은 회차 파일이 이미 있습니다.");
            GameOutcome = bits;
            string json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(this);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream)) writer.Write(json);
            return path;
        }
    }
}
