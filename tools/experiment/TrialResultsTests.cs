using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace VesperLab
{
    internal static class TrialResultsTests
    {
        private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; } catch (IOException) { rejected = true; }
            Expect(rejected, "invalid result was accepted");
        }
        private static void Fixture(Action<string, NoticeRecord> run)
        {
            string root = Path.Combine(Path.GetTempPath(), "zzz-result-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "trial.json");
                var trial = new NoticeRecord { Status = "submitted-not-game-verified", Mode = ExperimentPlan.LiveMode, StartCharacter = "아리아",
                    ActionIds = new[] { "one", "two", "three" }, ActionKeys = new[] { "RMB", "Space", "Space" },
                    DelaysMs = new[] { 1000.0, 2000.0, 3000.0 } };
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(trial));
                run(path, trial);
            }
            finally { Directory.Delete(root, true); }
        }
        public static void Run(Action<string, Action> test)
        {
            test("preparation notes correct independently, preserve old revisions and leave raw bytes unchanged", () => Fixture((path, trial) => {
                var serializer = new JavaScriptSerializer();
                trial.ProfileSnapshot.Preparation = new[] { new PreparationPress { Id = "prep", Label = "준비", Key = "RMB", AtMs = 1400, HoldMs = 150 } };
                File.WriteAllText(path, serializer.Serialize(trial));
                byte[] original = File.ReadAllBytes(path);
                TrialResultStore.Save(path, "111");
                var before = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(path + ".result.json"));
                string first = serializer.Serialize(((System.Collections.IList)before["revisions"])[0]);
                Expect(!first.Contains("preparationNote") && TrialResultStore.ReadPreparationNote(path) == "", "legacy absent note changed");
                TrialResultStore.Save(path, "111", "준비 중 피격");
                TrialResultStore.Save(path, "101");
                Expect(TrialResultStore.ReadPreparationNote(path) == "준비 중 피격", "bits-only correction cleared note");
                string unchanged = File.ReadAllText(path + ".result.json");
                TrialResultStore.Save(path, "101", "준비 중 피격");
                Expect(File.ReadAllText(path + ".result.json") == unchanged, "same note created a revision");
                TrialResultStore.Save(path, "101", "");
                var history = serializer.Deserialize<TrialResultHistory>(File.ReadAllText(path + ".result.json"));
                Expect(history.revisions.Count == 4 && TrialResultStore.ReadPreparationNote(path) == "", "note clearing or correction count failed");
                Expect(history.revisions[1].preparationSource.kind == "user-report" && history.revisions[3].preparationNote == "", "note provenance or explicit clear lost");
                Expect(original.SequenceEqual(File.ReadAllBytes(path)) && history.rawSha256 == ProfileStore.Sha256(original), "note changed raw evidence");
                TrialResultStore.CorrectStartCharacter(path, "수나");
                var after = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(path + ".result.json"));
                Expect(serializer.Serialize(((System.Collections.IList)after["revisions"])[0]) == first, "character correction changed old revision fingerprint");
                Expect(TrialResultStore.ReadPreparationNote(path) == "", "character correction lost explicit clear");
                Reject(() => TrialResultStore.Save(path, "101", new String('x', 501)));
                TrialResultStore.Save(path, "101", new String('x', 500));
                Expect(TrialResultStore.ReadPreparationNote(path).Length == 500, "500 character note rejected");
            }));
            test("pending preparation report is accepted separately from action bits", () => Fixture((path, trial) => {
                trial.ProfileSnapshot.Preparation = new[] { new PreparationPress { Id = "prep", Label = "준비", Key = "RMB", AtMs = 1400, HoldMs = 150 } };
                var pending = new PendingTrialStore(Path.GetDirectoryName(path));
                string raw = pending.Begin(trial); NoticeRecord.Save(raw, trial);
                string accepted = pending.Accept("101", "준비 위치가 달랐음");
                Expect(TrialResultStore.ReadBits(accepted) == "101" && TrialResultStore.ReadPreparationNote(accepted) == "준비 위치가 달랐음", "pending preparation report lost");
            }));
            test("legacy missing preparation never inherits selected profile for a preparation report", () => Fixture((path, trial) => {
                var serializer = new JavaScriptSerializer();
                var current = ProfileStore.Current.Preparation;
                try {
                    ProfileStore.Current.Preparation = new[] { new PreparationPress { Id = "prep", Label = "준비", Key = "RMB", AtMs = 1400, HoldMs = 150 } };
                    foreach (bool omitSnapshot in new[] { false, true }) {
                        var fields = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(serializer.Serialize(trial));
                        if (omitSnapshot) fields.Remove("ProfileSnapshot");
                        else ((System.Collections.Generic.Dictionary<string, object>)fields["ProfileSnapshot"]).Remove("Preparation");
                        File.WriteAllText(path, serializer.Serialize(fields));
                        Reject(() => TrialResultStore.Save(path, "111", "피격"));
                        var saved = TrialResultStore.ReadTrial(path);
                        Expect(omitSnapshot ? saved.ProfileSnapshot == null : saved.ProfileSnapshot.Preparation == null, "read invented preparation");
                        TrialResultStore.Save(path, "111");
                        Expect(TrialResultStore.ReadPreparationNote(path) == "", "missing preparation became success");
                        File.Delete(path + ".result.json");
                    }
                } finally { ProfileStore.Current.Preparation = current; }
            }));
            test("start character correction patches only its raw token, retains results and is idempotent", () => Fixture((path, trial) => {
                trial.StartCharacter = "아리아";
                trial.ProfileSnapshot.StartCharacter = "아리아";
                var fields = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(new JavaScriptSerializer().Serialize(trial));
                fields.Remove("StartCharacter");
                string rootToken = "\n  \"StartCharacter\":\"아리아\",";
                string original = "\uFEFF" + new JavaScriptSerializer().Serialize(fields).Insert(1, rootToken + "\n  \"UnknownField\":{\"StartCharacter\":\"keep\",\"escaped\":\"a\\\"b\"},\n");
                File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(original));
                DateTime created = DateTime.UtcNow.AddDays(-1); File.SetCreationTimeUtc(path, created); created = File.GetCreationTimeUtc(path);
                TrialResultStore.Save(path, "101"); TrialResultStore.Save(path, "111");
                var serializer = new JavaScriptSerializer();
                string revisions = serializer.Serialize(serializer.Deserialize<TrialResultHistory>(File.ReadAllText(path + ".result.json")).revisions);
                TrialResultStore.CorrectStartCharacter(path, "수나");
                string updated = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
                Expect(updated == original.Replace(rootToken, "\n  \"StartCharacter\":\"수나\","), "unrelated raw bytes changed");
                Expect(TrialResultStore.ReadStartCharacter(path) == "수나" && TrialResultStore.ReadBits(path) == "111", "corrected trial cannot be read");
                var history = serializer.Deserialize<TrialResultHistory>(File.ReadAllText(path + ".result.json"));
                Expect(serializer.Serialize(history.revisions) == revisions && history.startCharacterCorrections.Count == 1, "result history changed");
                Expect(history.rawSha256 == ProfileStore.Sha256(File.ReadAllBytes(path)) && File.GetCreationTimeUtc(path) == created, "hash or archive order changed");
                byte[] rawBefore = File.ReadAllBytes(path), resultBefore = File.ReadAllBytes(path + ".result.json");
                TrialResultStore.CorrectStartCharacter(path, "수나");
                Expect(rawBefore.SequenceEqual(File.ReadAllBytes(path)) && resultBefore.SequenceEqual(File.ReadAllBytes(path + ".result.json")), "same value changed files");
                TrialResultStore.CorrectStartCharacter(path, "아리아"); TrialResultStore.Save(path, "110");
                Expect(TrialResultStore.ReadBits(path) == "110" && TrialResultStore.ReadStartCharacter(path) == "아리아", "second correction or later result lost");
                history = serializer.Deserialize<TrialResultHistory>(File.ReadAllText(path + ".result.json"));
                Expect(history.startCharacterCorrections.Count == 2 && history.revisions.Count == 3, "correction history overwritten");
            }));
            test("invalid character corrections and preparation failures preserve both files", () => Fixture((path, trial) => {
                TrialResultStore.Save(path, "101");
                byte[] raw = File.ReadAllBytes(path), result = File.ReadAllBytes(path + ".result.json");
                foreach (string value in new[] { null, "", "  ", "a\nb", new String('x', 101) }) Reject(() => TrialResultStore.CorrectStartCharacter(path, value));
                string blocked = path + ".start-character.result.tmp";
                Directory.CreateDirectory(blocked);
                bool rejected = false;
                try { TrialResultStore.CorrectStartCharacter(path, "another-character"); } catch (UnauthorizedAccessException) { rejected = true; }
                Expect(rejected && raw.SequenceEqual(File.ReadAllBytes(path)) && result.SequenceEqual(File.ReadAllBytes(path + ".result.json")), "preparation failure changed saved trial");
                Directory.Delete(blocked);
                using (var locked = new FileStream(path + ".result.json.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) Reject(() => TrialResultStore.CorrectStartCharacter(path, "another-character"));
                File.WriteAllText(path, File.ReadAllText(path) + " ");
                Reject(() => TrialResultStore.CorrectStartCharacter(path, "another-character"));
                Expect(result.SequenceEqual(File.ReadAllBytes(path + ".result.json")), "hash mismatch changed result");
            }));
            test("interrupted start character publication recovers before reading or writing", () => Fixture((path, trial) => {
                trial.StartCharacter = "아리아"; File.WriteAllText(path, new JavaScriptSerializer().Serialize(trial));
                TrialResultStore.Save(path, "101");
                byte[] oldRaw = File.ReadAllBytes(path), oldResult = File.ReadAllBytes(path + ".result.json");
                DateTime created = File.GetCreationTimeUtc(path);
                TrialResultStore.CorrectStartCharacter(path, "수나");
                byte[] newRaw = File.ReadAllBytes(path), newResult = File.ReadAllBytes(path + ".result.json");
                foreach (bool rawPublished in new[] { false, true })
                {
                    File.WriteAllBytes(path, rawPublished ? newRaw : oldRaw); File.WriteAllBytes(path + ".result.json", oldResult);
                    File.WriteAllBytes(path + ".start-character.raw.tmp", newRaw); File.WriteAllBytes(path + ".start-character.result.tmp", newResult);
                    var txn = new StartCharacterTransaction { beforeRawSha256 = ProfileStore.Sha256(oldRaw), afterRawSha256 = ProfileStore.Sha256(newRaw),
                        beforeResultSha256 = ProfileStore.Sha256(oldResult), afterResultSha256 = ProfileStore.Sha256(newResult), rawCreationUtcTicks = created.Ticks };
                    File.WriteAllText(path + ".start-character.txn.json", new JavaScriptSerializer().Serialize(txn));
                    Expect(TrialResultStore.ReadStartCharacter(path) == "수나" && TrialResultStore.ReadBits(path) == "101", "interrupted correction did not recover");
                    Expect(newRaw.SequenceEqual(File.ReadAllBytes(path)) && newResult.SequenceEqual(File.ReadAllBytes(path + ".result.json")), "recovery changed publication");
                    Expect(!File.Exists(path + ".start-character.txn.json") && !File.Exists(path + ".start-character.raw.tmp"), "transaction files remain");
                }
            }));
            test("raw token correction rejects forged chain and missing recorded start character", () => Fixture((path, trial) => {
                TrialResultStore.Save(path, "101"); TrialResultStore.CorrectStartCharacter(path, "test-character");
                var serializer = new JavaScriptSerializer();
                var history = serializer.Deserialize<TrialResultHistory>(File.ReadAllText(path + ".result.json"));
                history.startCharacterCorrections[0].offset++;
                File.WriteAllText(path + ".result.json", serializer.Serialize(history));
                Reject(() => TrialResultStore.ReadBits(path)); Reject(() => TrialResultStore.CorrectStartCharacter(path, "another"));
                File.Delete(path + ".result.json");
                var fields = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(path));
                fields.Remove("StartCharacter"); File.WriteAllText(path, serializer.Serialize(fields));
                TrialResultStore.Save(path, "101"); Reject(() => TrialResultStore.CorrectStartCharacter(path, "another"));
            }));
            test("F8 recording is copied, frozen through commit and later result corrections", () => Fixture((path, trial) => {
                var at = DateTime.UtcNow;
                var snapshot = new ObsObservation { AtUtc = at.AddSeconds(-1), State = "ON", RecordingId = "recording-at-f8" };
                trial.FreezeRecordingAtF8(at, snapshot);
                snapshot.State = "OFF";
                trial.ObsObservations = new[] { snapshot };
                var pending = new PendingTrialStore(Path.GetDirectoryName(path));
                string raw = pending.Begin(trial); NoticeRecord.Save(raw, trial);
                string accepted = pending.Accept("111");
                TrialResultStore.Save(accepted, "101");
                var saved = TrialResultStore.ReadTrial(accepted);
                Expect(saved.Recording == "ON" && saved.RecordingAtF8.State == "ON" && saved.ObsObservations[0].State == "OFF", "later state changed F8 recording");
                Expect(saved.F8Utc == at.ToString("o") && saved.RecordingBasis == "f8-snapshot-unknown-off", "F8 provenance lost");
                foreach (string state in new[] { "OFF", "UNKNOWN", "PAUSED" })
                {
                    trial.FreezeRecordingAtF8(at, new ObsObservation { AtUtc = at, State = state });
                    trial.ObsObservations = new[] { new ObsObservation { AtUtc = at, State = "ON" } };
                    Expect(trial.Recording == "OFF" && trial.RecordingAtF8.State == state, "OFF policy or raw observation lost");
                }
                trial.FreezeRecordingAtF8(at, new ObsObservation { AtUtc = at.AddSeconds(-4), State = "ON" });
                Expect(trial.Recording == "OFF" && trial.RecordingAtF8.State == "UNKNOWN", "stale ON was trusted");
                trial.FreezeRecordingAtF8(at, null);
                Expect(trial.Recording == "OFF" && trial.RecordingAtF8.State == "UNKNOWN", "disconnected OBS required");
            }));
            test("pending trial requires valid results, commits once with final image reference", () => Fixture((path, trial) => {
                string root = Path.GetDirectoryName(path);
                var pending = new PendingTrialStore(root);
                string raw = pending.Begin(trial), image = Path.ChangeExtension(raw, ".png");
                trial.DetectedImage = image;
                NoticeRecord.Save(raw, trial); File.WriteAllText(image, "image-fixture");
                Reject(() => pending.Accept("11"));
                Expect(File.Exists(raw) && !Directory.Exists(Path.Combine(root, "notice-logs")), "invalid bits retained trial");
                string accepted = pending.Accept("101");
                Expect(!File.Exists(raw) && !File.Exists(image) && pending.CurrentPath == null, "pending trial remained active");
                var saved = TrialResultStore.ReadTrial(accepted);
                Expect(saved.TrialId == trial.TrialId && saved.DelaysMs.SequenceEqual(trial.DelaysMs), "trial identity/settings changed");
                Expect(saved.DetectedImage == Path.ChangeExtension(accepted, ".png") && File.Exists(saved.DetectedImage), "stale image reference");
                Expect(TrialResultStore.ReadBits(accepted) == "101", "result/hash mismatch");
                TrialResultStore.Save(accepted, "101");
                Expect(new JavaScriptSerializer().Deserialize<TrialResultHistory>(File.ReadAllText(accepted + ".result.json")).revisions.Count == 1, "duplicate acceptance");
                pending.Discard(); Expect(File.Exists(accepted), "accepted trial discarded");
            }));
            test("discard and restart cleanup remove only pending files, preserve archive and shared media", () => Fixture((path, trial) => {
                string root = Path.GetDirectoryName(path), shared = Path.Combine(root, "shared-video.mp4");
                File.WriteAllText(shared, "shared");
                var pending = new PendingTrialStore(root);
                string raw = pending.Begin(trial); trial.DetectedImage = shared; NoticeRecord.Save(raw, trial);
                File.WriteAllText(Path.ChangeExtension(raw, ".png"), "private");
                pending.Discard();
                Expect(!File.Exists(raw) && !File.Exists(Path.ChangeExtension(raw, ".png")) && File.Exists(path) && File.Exists(shared), "discard escaped pending ownership");
                raw = pending.Begin(trial); NoticeRecord.Save(raw, trial);
                new PendingTrialStore(root).DiscardAbandoned();
                Expect(!File.Exists(raw) && File.Exists(path) && File.Exists(shared), "restart cleanup changed history");
            }));
            test("failed promotion rolls back created files and permits retry", () => Fixture((path, trial) => {
                string root = Path.GetDirectoryName(path);
                var pending = new PendingTrialStore(root);
                string raw = pending.Begin(trial); NoticeRecord.Save(raw, trial);
                File.WriteAllText(Path.ChangeExtension(raw, ".png"), "private");
                string destination = Path.Combine(root, "notice-logs", Path.GetFileName(raw).Replace("pending-", "trial-"));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                // Fail the last move, after image/result publication, deterministically.
                Directory.CreateDirectory(destination);
                Reject(() => pending.Accept("111"));
                Expect(File.Exists(raw) && pending.CurrentPath == raw && !File.Exists(destination + ".result.json") && !File.Exists(Path.ChangeExtension(destination, ".png")), "partial commit or lost retry");
                Directory.Delete(destination);
                Expect(TrialResultStore.ReadBits(pending.Accept("111")) == "111", "retry failed");
            }));
            test("saved trial reading is independent of invalid current baseline schedule", () => Fixture((path, trial) => {
                var original = ProfileStore.Snapshot();
                try {
                    var changed = ProfileStore.Snapshot(); changed.Actions[1].Timing = changed.Actions[0].Timing;
                    ProfileStore.Apply(changed);
                    TrialResultStore.Save(path, "101");
                    Expect(TrialResultStore.ReadBits(path) == "101", "current candidate collision broke historical result");
                } finally { ProfileStore.Apply(original); }
            }));
            test("binary results require recorded count and only 0/1", () => Fixture((path, trial) => {
                foreach (string bits in new[] { null, "", "1", "1011", "10x", "1 1", "101\n", "１０１" }) Reject(() => TrialResultStore.Save(path, bits));
                Expect(!File.Exists(path + ".result.json"), "invalid input wrote results");
                Expect(TrialResultStore.ReadBits(path) == "", "new trial already reported");
                TrialResultStore.Save(path, "101");
                Expect(TrialResultStore.ReadBits(path) == "101", "round trip mismatch");
            }));
            test("binary corrections retain history, source and unchanged raw bytes", () => Fixture((path, trial) => {
                byte[] original = File.ReadAllBytes(path);
                TrialResultStore.Save(path, "101"); TrialResultStore.Save(path, "110"); TrialResultStore.Save(path, "110");
                var history = new JavaScriptSerializer().Deserialize<TrialResultHistory>(File.ReadAllText(path + ".result.json"));
                Expect(history.revisions.Count == 2, "correction history/idempotence mismatch");
                Expect(history.revisions[0].bits == "101" && history.revisions[1].bits == "110", "history overwritten");
                Expect(history.revisions.All(r => r.actions.All(a => a.source.kind == "user-report")), "report source lost");
                Expect(history.revisions[1].actions[2].outcome == "failure", "zero is not failure");
                Expect(original.SequenceEqual(File.ReadAllBytes(path)), "raw bytes changed");
                Expect(history.rawSha256 == ProfileStore.Sha256(original), "raw hash differs");
            }));
            test("result count belongs to saved trial after selected profile changes", () => Fixture((path, trial) => {
                var previous = ProfileStore.Current.Actions;
                try
                {
                    ProfileStore.Current.Actions = previous.Take(1).ToArray();
                    Expect(TrialResultStore.ReadTrial(path).ActionIds.Length == 3, "selected profile changed recorded actions");
                    Reject(() => TrialResultStore.Save(path, "1"));
                    TrialResultStore.Save(path, "010");
                    Expect(TrialResultStore.ReadBits(path) == "010", "saved count used current profile");
                }
                finally { ProfileStore.Current.Actions = previous; }
            }));
            test("active and no-input trials cannot receive binary results", () => Fixture((path, trial) => {
                foreach (string status in new[] { "armed", "detected", "observed-no-input", "unknown" })
                {
                    trial.Status = status; File.WriteAllText(path, new JavaScriptSerializer().Serialize(trial));
                    Reject(() => TrialResultStore.Save(path, "111"));
                }
                trial.Status = "cancelled"; trial.Mode = "observe";
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(trial)); Reject(() => TrialResultStore.Save(path, "111"));
                Expect(!File.Exists(path + ".result.json"), "unsupported trial wrote result");
            }));
            test("deleted raw trial is never recreated on result correction", () => Fixture((path, trial) => {
                TrialResultStore.Save(path, "100"); string before = File.ReadAllText(path + ".result.json");
                File.Delete(path); Reject(() => TrialResultStore.Save(path, "111"));
                Expect(!File.Exists(path), "deleted raw restored");
                Expect(File.ReadAllText(path + ".result.json") == before, "orphan sidecar changed");
            }));
            test("result sidecar cannot be attached to different raw trial", () => Fixture((path, trial) => {
                TrialResultStore.Save(path, "100");
                trial.TrialId = "other-trial"; File.WriteAllText(path, new JavaScriptSerializer().Serialize(trial));
                Reject(() => TrialResultStore.Save(path, "111")); Reject(() => TrialResultStore.ReadBits(path));
            }));
            test("missing recorded ActionIds never fall back to current profile", () => Fixture((path, trial) => {
                var serializer = new JavaScriptSerializer();
                var fields = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(path));
                fields.Remove("ActionIds"); File.WriteAllText(path, serializer.Serialize(fields));
                Reject(() => TrialResultStore.Save(path, new String('1', ProfileStore.Current.Actions.Length)));
            }));
        }
    }
}
