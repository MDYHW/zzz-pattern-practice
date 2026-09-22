using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace VesperLab
{
    // Only confirmed trials enter notice-logs. Raw JSON is published last, after
    // its result and optional image; failed commits leave the pending trial retryable.
    public sealed class PendingTrialStore
    {
        private readonly string pendingDirectory, archiveDirectory;
        public string CurrentPath { get; private set; }
        public PendingTrialStore(string root)
        {
            pendingDirectory = Path.GetFullPath(Path.Combine(root, "pending-trials"));
            archiveDirectory = Path.GetFullPath(Path.Combine(root, "notice-logs"));
        }
        public string Begin(NoticeRecord record)
        {
            if (CurrentPath != null) throw new InvalidOperationException("이전 임시 회차를 먼저 처리하세요.");
            Directory.CreateDirectory(pendingDirectory);
            CurrentPath = Path.Combine(pendingDirectory, "pending-" + Guid.NewGuid().ToString("N") + ".json");
            return CurrentPath;
        }
        private void DeletePending(string path)
        {
            if (!String.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), pendingDirectory, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("임시 회차 폴더 밖의 파일은 삭제할 수 없습니다.");
            // Never follow the image path recorded in JSON: it may name shared data.
            foreach (string file in new[] { Path.ChangeExtension(path, ".png"), path + ".tmp", path + ".commit.result.json", path + ".commit", path })
                if (File.Exists(file)) File.Delete(file);
        }
        public void DiscardAbandoned()
        {
            if (!Directory.Exists(pendingDirectory)) return;
            foreach (string path in Directory.GetFiles(pendingDirectory, "pending-*.json"))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^pending-[a-f0-9]{32}\.json$")) DeletePending(path);
        }
        public void Discard()
        {
            if (CurrentPath == null) return;
            DeletePending(CurrentPath); CurrentPath = null;
        }
        public string Accept(string bits, string preparationNote = null)
        {
            if (CurrentPath == null) throw new InvalidOperationException("확정할 임시 회차가 없습니다.");
            var record = TrialResultStore.ReadTrial(CurrentPath);
            TrialResultStore.ValidateBits(bits, record.ActionIds.Length);
            Directory.CreateDirectory(archiveDirectory);
            string destination = Path.Combine(archiveDirectory, Path.GetFileName(CurrentPath).Replace("pending-", "trial-"));
            string image = Path.ChangeExtension(CurrentPath, ".png"), finalImage = Path.ChangeExtension(destination, ".png");
            string stage = CurrentPath + ".commit", result = destination + ".result.json";
            if (File.Exists(destination) || File.Exists(finalImage) || File.Exists(result)) throw new IOException("같은 이름의 보관 파일이 이미 있습니다.");
            bool imageCreated = false, resultCreated = false, committed = false;
            try
            {
                record.DetectedImage = File.Exists(image) ? finalImage : "";
                File.WriteAllText(stage, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(record), new UTF8Encoding(false));
                TrialResultStore.Save(stage, bits, preparationNote);
                if (File.Exists(image)) { File.Copy(image, finalImage); imageCreated = true; }
                File.Move(stage + ".result.json", result); resultCreated = true;
                File.Move(stage, destination); committed = true;
            }
            finally
            {
                if (!committed)
                {
                    if (resultCreated) File.Delete(result);
                    if (imageCreated) File.Delete(finalImage);
                }
                if (File.Exists(stage + ".result.json")) File.Delete(stage + ".result.json");
                if (File.Exists(stage)) File.Delete(stage);
            }
            // The archive is committed. If cleanup is temporarily blocked, startup
            // cleanup can remove the remaining private copy without another archive.
            string pending = CurrentPath; CurrentPath = null;
            try { DeletePending(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return destination;
        }
    }
    public sealed class TrialResultSource
    {
        public string kind = "user-report", reference = "회차 종료 후 사용자 직접 입력";
    }
    public sealed class TrialResultAction
    {
        public int attack;
        public string outcome;
        public TrialResultSource source = new TrialResultSource();
    }
    public sealed class TrialResultRevision
    {
        public string at, bits, reason;
        public TrialResultAction[] actions;
        public string preparationNote;
        public TrialResultSource preparationSource;
    }
    public sealed class StartCharacterCorrection
    {
        public string at, beforeRawSha256, afterRawSha256, before, after, beforeJson, afterJson;
        public int offset;
    }
    internal sealed class StartCharacterTransaction
    {
        public string beforeRawSha256, afterRawSha256, beforeResultSha256, afterResultSha256;
        public long rawCreationUtcTicks;
    }
    public sealed class TrialResultHistory
    {
        public int schemaVersion = 1;
        public string trialId, rawSha256;
        public List<TrialResultRevision> revisions = new List<TrialResultRevision>();
        public List<StartCharacterCorrection> startCharacterCorrections;
    }
    public static class TrialResultStore
    {
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }; }
        private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
        private static byte[] ReadBytes(FileStream stream)
        {
            using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
        }
        private static NoticeRecord Parse(byte[] bytes)
        {
            string json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            var serializer = Serializer();
            var fields = serializer.Deserialize<Dictionary<string, object>>(json);
            // NoticeRecord initializers use the selected profile. Required recorded fields must
            // actually exist in the raw JSON, never fall back to those current-profile defaults.
            foreach (string name in new[] { "TrialId", "Status", "ActionIds", "DelaysMs", "BossId", "Pattern" })
                Require(fields != null && fields.ContainsKey(name) && fields[name] != null, "회차 원본에 " + name + " 정보가 없습니다.");
            var trial = serializer.Deserialize<NoticeRecord>(json);
            if (!fields.ContainsKey("ProfileSnapshot") || fields["ProfileSnapshot"] == null) trial.ProfileSnapshot = null;
            if (!HasRecordedPreparation(bytes) && trial.ProfileSnapshot != null) trial.ProfileSnapshot.Preparation = null;
            Require(!String.IsNullOrWhiteSpace(trial.TrialId), "회차 ID가 없습니다.");
            Require(new[] { "completed", "submitted-not-game-verified", "failed", "cancelled", "release-failed" }.Contains(trial.Status), "입력 실험이 종료된 회차만 결과를 저장할 수 있습니다.");
            Require(trial.Mode != "observe" && trial.Mode != "observe-only", "무입력 관찰 회차에는 성공·실패 결과를 저장할 수 없습니다.");
            Require(trial.ActionIds != null && trial.ActionIds.Length > 0 && trial.ActionIds.All(id => !String.IsNullOrWhiteSpace(id)) && trial.ActionIds.Distinct().Count() == trial.ActionIds.Length, "원본 회차의 동작 목록이 잘못되었습니다.");
            Require(trial.DelaysMs != null && trial.DelaysMs.Length == trial.ActionIds.Length, "원본 회차의 동작 수와 시각 수가 다릅니다.");
            return trial;
        }
        internal static void ValidateBits(string bits, int count)
        {
            Require(bits != null && bits.Length == count && bits.All(c => c == '0' || c == '1'), "결과는 정확히 " + count + "자리의 0 또는 1이어야 합니다. (0 실패 / 1 성공)");
        }
        private static TrialResultHistory ReadHistory(string path, NoticeRecord trial, byte[] bytes)
        {
            if (!File.Exists(path)) return new TrialResultHistory { trialId = trial.TrialId, rawSha256 = ProfileStore.Sha256(bytes) };
            var history = Serializer().Deserialize<TrialResultHistory>(File.ReadAllText(path));
            Require(history != null && history.schemaVersion == 1 && history.trialId == trial.TrialId && history.rawSha256 == ProfileStore.Sha256(bytes), "결과 기록의 회차 ID 또는 원본 해시가 다릅니다.");
            Require(history.revisions != null && history.revisions.Count > 0, "결과 정정 이력이 잘못되었습니다.");
            foreach (var revision in history.revisions)
            {
                Require(revision != null, "빈 결과 이력입니다.");
                ValidateBits(revision.bits, trial.ActionIds.Length);
                ValidatePreparationNote(revision.preparationNote, bytes);
                Require(revision.preparationNote == null ? revision.preparationSource == null
                    : revision.preparationSource != null && revision.preparationSource.kind == "user-report" && !String.IsNullOrWhiteSpace(revision.preparationSource.reference), "준비 메모의 사용자 보고 출처가 잘못되었습니다.");
                Require(!String.IsNullOrWhiteSpace(revision.at) && !String.IsNullOrWhiteSpace(revision.reason) && revision.actions != null && revision.actions.Length == trial.ActionIds.Length, "결과 정정 이력이 잘못되었습니다.");
                for (int i = 0; i < revision.actions.Length; i++)
                {
                    var action = revision.actions[i];
                    Require(action != null && action.attack == i && action.outcome == (revision.bits[i] == '1' ? "success" : "failure") && action.source != null && action.source.kind == "user-report" && !String.IsNullOrWhiteSpace(action.source.reference), "결과 동작과 사용자 보고 이력이 다릅니다.");
                }
            }
            if (history.startCharacterCorrections != null)
            {
                string original = Encoding.UTF8.GetString(bytes);
                foreach (var correction in history.startCharacterCorrections.AsEnumerable().Reverse())
                {
                    Require(correction != null && !String.IsNullOrWhiteSpace(correction.at) && correction.beforeJson != null && correction.afterJson != null, "시작 캐릭터 정정 이력이 잘못되었습니다.");
                    Require(ProfileStore.Sha256(Encoding.UTF8.GetBytes(original)) == correction.afterRawSha256, "시작 캐릭터 정정 해시 연결이 잘못되었습니다.");
                    int[] token = StartCharacterToken(original);
                    Require(token[0] == correction.offset && original.Substring(token[0], token[1]) == correction.afterJson, "시작 캐릭터 정정 위치가 잘못되었습니다.");
                    Require(Serializer().Deserialize<string>(correction.afterJson) == correction.after && Serializer().Deserialize<string>(correction.beforeJson) == correction.before, "시작 캐릭터 정정 값이 잘못되었습니다.");
                    original = original.Substring(0, token[0]) + correction.beforeJson + original.Substring(token[0] + token[1]);
                    Require(ProfileStore.Sha256(Encoding.UTF8.GetBytes(original)) == correction.beforeRawSha256, "시작 캐릭터 정정 이전 해시가 다릅니다.");
                    int[] prior = StartCharacterToken(original);
                    Require(prior[0] == correction.offset && original.Substring(prior[0], prior[1]) == correction.beforeJson, "시작 캐릭터 외의 필드를 정정할 수 없습니다.");
                }
            }
            return history;
        }
        private static FileStream Lock(string rawPath)
        {
            return new FileStream(rawPath + ".result.json.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        private static string HashFile(string path) { return ProfileStore.Sha256(File.ReadAllBytes(path)); }
        private static void RemoveIfPresent(string path) { if (File.Exists(path)) File.Delete(path); }
        private static void RecoverStartCharacter(string rawPath)
        {
            string marker = rawPath + ".start-character.txn.json";
            if (!File.Exists(marker)) return;
            var txn = Serializer().Deserialize<StartCharacterTransaction>(File.ReadAllText(marker));
            Require(txn != null, "시작 캐릭터 정정 복구 정보가 잘못되었습니다.");
            string result = rawPath + ".result.json", rawStage = rawPath + ".start-character.raw.tmp", resultStage = rawPath + ".start-character.result.tmp";
            string rawHash = HashFile(rawPath), resultHash = HashFile(result);
            Require(rawHash == txn.beforeRawSha256 || rawHash == txn.afterRawSha256, "정정 중 원본이 외부에서 변경되어 복구할 수 없습니다.");
            Require(resultHash == txn.beforeResultSha256 || resultHash == txn.afterResultSha256, "정정 중 결과가 외부에서 변경되어 복구할 수 없습니다.");
            // Validate both pending publications before replacing either file.
            if (rawHash != txn.afterRawSha256) Require(HashFile(rawStage) == txn.afterRawSha256, "정정 원본 임시파일 해시가 다릅니다.");
            if (resultHash != txn.afterResultSha256) Require(HashFile(resultStage) == txn.afterResultSha256, "정정 결과 임시파일 해시가 다릅니다.");
            if (rawHash != txn.afterRawSha256) File.Replace(rawStage, rawPath, null);
            if (resultHash != txn.afterResultSha256) File.Replace(resultStage, result, null);
            File.SetCreationTimeUtc(rawPath, new DateTime(txn.rawCreationUtcTicks, DateTimeKind.Utc));
            File.Delete(marker);
            RemoveIfPresent(rawStage); RemoveIfPresent(resultStage);
        }
        public static NoticeRecord ReadTrial(string rawPath)
        {
            using (var gate = Lock(rawPath)) { RecoverStartCharacter(rawPath); return Parse(File.ReadAllBytes(rawPath)); }
        }
        private static Dictionary<string, object> Fields(byte[] bytes)
        {
            return Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        }
        private static bool HasRecordedPreparation(byte[] bytes)
        {
            object snapshot, preparation;
            var fields = Fields(bytes);
            var profile = fields.TryGetValue("ProfileSnapshot", out snapshot) ? snapshot as Dictionary<string, object> : null;
            var rows = profile != null && profile.TryGetValue("Preparation", out preparation) ? preparation as System.Collections.IList : null;
            return rows != null && rows.Count > 0;
        }
        private static void ValidatePreparationNote(string note, byte[] bytes)
        {
            Require(note == null || note.Length <= 500, "준비 메모는 500자 이하여야 합니다.");
            Require(String.IsNullOrEmpty(note) || HasRecordedPreparation(bytes), "준비 대응이 기록된 회차에만 준비 메모를 저장할 수 있습니다.");
        }
        private static string SerializeHistory(TrialResultHistory history)
        {
            var serializer = Serializer();
            var fields = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(history));
            foreach (Dictionary<string, object> revision in (System.Collections.IList)fields["revisions"])
                if (revision["preparationNote"] == null) { revision.Remove("preparationNote"); revision.Remove("preparationSource"); }
            // Absent fields in older revisions must stay absent: imports fingerprint each revision.
            return serializer.Serialize(fields);
        }
        private static string RecordedStartCharacter(byte[] bytes)
        {
            var fields = Fields(bytes); object value;
            if (fields.TryGetValue("StartCharacter", out value) && value is string && !String.IsNullOrWhiteSpace((string)value)) return (string)value;
            object snapshot;
            if (fields.TryGetValue("ProfileSnapshot", out snapshot))
            {
                var profile = snapshot as Dictionary<string, object>;
                if (profile != null && profile.TryGetValue("StartCharacter", out value) && value is string) return (string)value;
            }
            return "";
        }
        public static string ReadStartCharacter(string rawPath)
        {
            using (var gate = Lock(rawPath)) { RecoverStartCharacter(rawPath); byte[] bytes = File.ReadAllBytes(rawPath); Parse(bytes); return RecordedStartCharacter(bytes); }
        }
        private static int StringEnd(string text, int start)
        {
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i + 1;
            }
            throw new ArgumentException("원본 JSON 문자열이 끝나지 않았습니다.");
        }
        private static int[] StartCharacterToken(string text)
        {
            int depth = 0; int[] found = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' || c == '[') { depth++; continue; }
                if (c == '}' || c == ']') { depth--; continue; }
                if (c != '"') continue;
                int end = StringEnd(text, i), next = end;
                while (next < text.Length && Char.IsWhiteSpace(text[next])) next++;
                if (depth == 1 && next < text.Length && text[next] == ':' && Serializer().Deserialize<string>(text.Substring(i, end - i)) == "StartCharacter")
                {
                    Require(found == null, "시작 캐릭터 필드가 중복되어 정정할 수 없습니다.");
                    next++; while (next < text.Length && Char.IsWhiteSpace(text[next])) next++;
                    Require(next < text.Length && text[next] == '"', "시작 캐릭터가 문자열인 원본만 정정할 수 있습니다.");
                    found = new[] { next, StringEnd(text, next) - next };
                }
                i = end - 1;
            }
            Require(found != null, "원본에 시작 캐릭터 필드가 없어 정정할 수 없습니다.");
            return found;
        }
        public static void CorrectStartCharacter(string rawPath, string value)
        {
            value = value == null ? "" : value.Trim();
            Require(value.Length > 0 && value.Length <= 100 && !value.Any(Char.IsControl), "시작 캐릭터를 1~100자의 이름으로 입력하세요.");
            using (var gate = Lock(rawPath))
            {
                RecoverStartCharacter(rawPath);
                byte[] bytes = File.ReadAllBytes(rawPath); var trial = Parse(bytes);
                string result = rawPath + ".result.json";
                Require(File.Exists(result), "결과가 보관된 회차만 시작 캐릭터를 정정할 수 있습니다.");
                var history = ReadHistory(result, trial, bytes);
                string text = Encoding.UTF8.GetString(bytes);
                Require(Encoding.UTF8.GetBytes(text).SequenceEqual(bytes), "원본은 유효한 UTF-8이어야 합니다.");
                int[] token = StartCharacterToken(text);
                string beforeJson = text.Substring(token[0], token[1]), before = Serializer().Deserialize<string>(beforeJson);
                if (before == value) return;
                string afterJson = Serializer().Serialize(value);
                byte[] updated = Encoding.UTF8.GetBytes(text.Substring(0, token[0]) + afterJson + text.Substring(token[0] + token[1]));
                if (history.startCharacterCorrections == null) history.startCharacterCorrections = new List<StartCharacterCorrection>();
                history.startCharacterCorrections.Add(new StartCharacterCorrection {
                    at = DateTime.UtcNow.ToString("o"), beforeRawSha256 = ProfileStore.Sha256(bytes), afterRawSha256 = ProfileStore.Sha256(updated),
                    before = before, after = value, offset = token[0], beforeJson = beforeJson, afterJson = afterJson
                });
                history.rawSha256 = ProfileStore.Sha256(updated);
                byte[] resultBytes = Encoding.UTF8.GetBytes(SerializeHistory(history));
                string marker = rawPath + ".start-character.txn.json", rawStage = rawPath + ".start-character.raw.tmp", resultStage = rawPath + ".start-character.result.tmp";
                string markerStage = marker + ".tmp";
                bool published = false;
                try
                {
                    File.WriteAllBytes(rawStage, updated); File.WriteAllBytes(resultStage, resultBytes);
                    var txn = new StartCharacterTransaction { beforeRawSha256 = ProfileStore.Sha256(bytes), afterRawSha256 = history.rawSha256,
                        beforeResultSha256 = HashFile(result), afterResultSha256 = ProfileStore.Sha256(resultBytes), rawCreationUtcTicks = File.GetCreationTimeUtc(rawPath).Ticks };
                    File.WriteAllText(markerStage, Serializer().Serialize(txn), new UTF8Encoding(false));
                    File.Move(markerStage, marker); published = true;
                    RecoverStartCharacter(rawPath);
                }
                finally
                {
                    // Once the journal is visible, keep its staged files for the next access.
                    if (!published) { RemoveIfPresent(rawStage); RemoveIfPresent(resultStage); }
                    RemoveIfPresent(markerStage);
                }
            }
        }
        public static string ReadBits(string rawPath)
        {
            using (var gate = Lock(rawPath))
            {
                RecoverStartCharacter(rawPath);
                using (var raw = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] bytes = ReadBytes(raw); var trial = Parse(bytes);
                    var history = ReadHistory(rawPath + ".result.json", trial, bytes);
                    return history.revisions.Count == 0 ? "" : history.revisions.Last().bits;
                }
            }
        }
        public static string ReadPreparationNote(string rawPath)
        {
            using (var gate = Lock(rawPath))
            {
                RecoverStartCharacter(rawPath);
                byte[] bytes = File.ReadAllBytes(rawPath); var trial = Parse(bytes);
                var history = ReadHistory(rawPath + ".result.json", trial, bytes);
                return history.revisions.Count == 0 ? "" : history.revisions.Last().preparationNote ?? "";
            }
        }
        public static void Save(string rawPath, string bits, string preparationNote = null)
        {
            using (var gate = Lock(rawPath))
            {
                RecoverStartCharacter(rawPath);
                // All readers and writers take the sidecar lock first; no inverse lock order.
                using (var raw = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] bytes = ReadBytes(raw); var trial = Parse(bytes);
                    ValidateBits(bits, trial.ActionIds.Length);
                    string path = rawPath + ".result.json";
                    var history = ReadHistory(path, trial, bytes);
                    var previous = history.revisions.LastOrDefault();
                    preparationNote = preparationNote ?? (previous == null ? null : previous.preparationNote);
                    ValidatePreparationNote(preparationNote, bytes);
                    if (previous != null && previous.bits == bits && (previous.preparationNote ?? "") == (preparationNote ?? "")) return;
                    history.revisions.Add(new TrialResultRevision {
                        at = DateTime.UtcNow.ToString("o"), bits = bits,
                        preparationNote = preparationNote,
                        preparationSource = preparationNote == null ? null : new TrialResultSource(),
                        reason = history.revisions.Count == 0 ? "사용자 최초 결과 입력" : "사용자 결과 정정",
                        actions = bits.Select((bit, index) => new TrialResultAction { attack = index, outcome = bit == '1' ? "success" : "failure" }).ToArray()
                    });
                    string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temp, SerializeHistory(history), new UTF8Encoding(false));
                        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
        }
    }
}
