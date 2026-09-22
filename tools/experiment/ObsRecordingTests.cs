using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VesperLab
{
    internal static class ObsRecordingTests
    {
        // In-memory websocket substitute: production handshake, receive loop, correlation and ledger run unchanged.
        private sealed class Wire : IObsWire
        {
            private readonly object gate = new object();
            private readonly Queue<string> incoming = new Queue<string>();
            private TaskCompletionSource<string> waiting;
            public readonly List<string> Sent = new List<string>();
            public bool Active, Paused, Reply = true, Auth, BadStatus;
            public string LastRequest;
            public Task ConnectAsync(Uri endpoint, CancellationToken cancel)
            {
                Push(Auth ? "{\"op\":0,\"d\":{\"rpcVersion\":1,\"authentication\":{\"salt\":\"lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=\",\"challenge\":\"+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=\"}}}"
                    : "{\"op\":0,\"d\":{\"rpcVersion\":1}}");
                return Task.FromResult(true);
            }
            public Task SendAsync(string json, CancellationToken cancel)
            {
                lock (gate) Sent.Add(json);
                var message = ObsRecordingMonitor.Parse(json);
                int op = (int)message["op"];
                if (op == 1) Push("{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}");
                else
                {
                    var data = (Dictionary<string, object>)message["d"];
                    Require(op == 6 && (string)data["requestType"] == "GetRecordStatus", "observer sent a control request");
                    LastRequest = (string)data["requestId"];
                    if (Reply) Respond(LastRequest);
                }
                return Task.FromResult(true);
            }
            public void Respond(string requestId)
            {
                Push(ObsRecordingMonitor.Json(new { op = 7, d = new { requestType = "GetRecordStatus", requestId = requestId,
                    requestStatus = new { result = !BadStatus, code = BadStatus ? 500 : 100 },
                    responseData = new { outputActive = Active, outputPaused = Paused } } }));
            }
            public Task<string> ReceiveAsync(CancellationToken cancel)
            {
                lock (gate)
                {
                    if (incoming.Count != 0) return Task.FromResult(incoming.Dequeue());
                    waiting = new TaskCompletionSource<string>();
                    var pending = waiting;
                    cancel.Register(() => pending.TrySetCanceled());
                    return pending.Task;
                }
            }
            public void Push(string json)
            {
                TaskCompletionSource<string> pending;
                lock (gate) { pending = waiting; waiting = null; if (pending == null) incoming.Enqueue(json); }
                if (pending != null) pending.TrySetResult(json);
            }
            public void State(string outputState, string path)
            {
                Push(ObsRecordingMonitor.Json(new { op = 5, d = new { eventType = "RecordStateChanged",
                    eventData = new { outputState = "OBS_WEBSOCKET_OUTPUT_" + outputState, outputActive = outputState != "STOPPED", outputPath = path } } }));
            }
            public void Split(string path)
            {
                Push(ObsRecordingMonitor.Json(new { op = 5, d = new { eventType = "RecordFileChanged", eventData = new { newOutputPath = path } } }));
            }
            public void Dispose()
            {
                TaskCompletionSource<string> pending;
                lock (gate) { pending = waiting; waiting = null; }
                if (pending != null) pending.TrySetCanceled();
            }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Await(Task task) { Require(task.Wait(3000), "fake OBS operation timed out"); }
        private static void Eventually(Func<bool> condition) { Require(SpinWait.SpinUntil(condition, 3000), "fake OBS observation timed out"); }
        private static void InLedger(Action<string> action)
        {
            string path = Path.Combine(Path.GetTempPath(), "vesper-obs-test-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(path); action(path); }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }

        public static void Run(Action<string, Action> test)
        {
            test("OBS settings discover native credentials and persist a protected override", () => InLedger(path =>
            {
                string native = Path.Combine(path, "native.json");
                var settings = ExperimentSettings.Load(path, native);
                Require(settings.Endpoint == "ws://127.0.0.1:4455" && settings.Password() == "", "no-config default failed");
                File.WriteAllText(native, "{\"server_port\":4456,\"auth_required\":false,\"server_password\":\"unused\"}");
                settings = ExperimentSettings.Load(path, native);
                Require(settings.Endpoint.EndsWith(":4456") && settings.Password() == "", "unauthenticated config used password");
                File.WriteAllText(native, "{\"server_port\":4457,\"auth_required\":true,\"server_password\":\"fixture-password\"}");
                settings = ExperimentSettings.Load(path, native);
                Require(settings.Password() == "fixture-password", "native password DPAPI roundtrip failed");
                string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(settings);
                Require(!json.Contains("fixture-password"), "settings contain plaintext password");
                File.WriteAllText(Path.Combine(path, "settings.json"), json); File.Delete(native);
                Require(ExperimentSettings.Load(path, native).Password() == "fixture-password" && ExperimentSettings.Load(path, native).Endpoint.EndsWith(":4457"), "saved override not restored");
            }));
            test("OBS ledger write failure preserves state and reports missing evidence", () => InLedger(path =>
            {
                string blocked = Path.Combine(path, "not-a-directory"); File.WriteAllText(blocked, "fixture");
                var wire = new Wire { Active = true };
                using (var monitor = new ObsRecordingMonitor(blocked, () => wire, 500))
                {
                    Await(monitor.ConnectAsync("ws://127.0.0.1:4455", ""));
                    Require(monitor.Connected && monitor.Snapshot().State == "ON" && monitor.StatusText.Contains("could not be saved"), "ledger failure lost observation or warning");
                    wire.Active = false; Await(monitor.RefreshAsync());
                    Require(monitor.Snapshot().State == "OFF", "observer stopped after ledger failure");
                }
            }));
            test("OBS classification preserves unknown and paused intervals", () =>
            {
                Func<string, ObsObservation> obs = s => new ObsObservation { State = s, AtUtc = DateTime.UtcNow };
                Require(ObsRecordingMonitor.Classify(new ObsObservation[0]) == "UNKNOWN", "empty must be unknown");
                Require(ObsRecordingMonitor.Classify(new[] { obs("ON"), obs("ON") }) == "ON", "on classification");
                Require(ObsRecordingMonitor.Classify(new[] { obs("OFF"), obs("OFF") }) == "OFF", "off classification");
                Require(ObsRecordingMonitor.Classify(new[] { obs("ON"), obs("OFF") }) == "MIXED", "mixed classification");
                Require(ObsRecordingMonitor.Classify(new[] { obs("PAUSED") }) == "MIXED", "pause must not be on or off");
                Require(ObsRecordingMonitor.Classify(new[] { obs("ON"), obs("UNKNOWN"), obs("OFF") }) == "UNKNOWN", "gap must remain unknown");
            });
            test("OBS authenticates using official challenge vector and read-only request", () => InLedger(path =>
            {
                // Derived independently from the documentation's password/salt/challenge example.
                string expected = "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=";
                Require(ObsRecordingMonitor.Authentication("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=") == expected, "challenge vector mismatch");
                var wire = new Wire { Auth = true };
                using (var monitor = new ObsRecordingMonitor(path, () => wire, 500))
                {
                    Await(monitor.ConnectAsync("ws://127.0.0.1:4455", "supersecretpassword"));
                    Require(monitor.Connected && monitor.Snapshot().State == "OFF", "initial off query failed");
                    Require(wire.Sent[0].Contains(expected) && !wire.Sent[0].Contains("supersecretpassword"), "authentication plaintext leaked");
                    Require(Directory.GetFiles(path).Length == 0, "off observation invented a recording");
                    wire.Active = true; wire.Paused = true;
                    Await(monitor.RefreshAsync());
                    Require(monitor.Snapshot().State == "PAUSED", "paused query became ON/OFF");
                }
            }));
            test("OBS final path links an earlier recording ID and split paths survive", () => InLedger(path =>
            {
                var wire = new Wire();
                using (var monitor = new ObsRecordingMonitor(path, () => wire, 500))
                {
                    Await(monitor.ConnectAsync("ws://127.0.0.1:4455", ""));
                    wire.State("STARTED", null);
                    Eventually(() => monitor.Snapshot().State == "ON");
                    ObsObservation trial = monitor.Snapshot();
                    Require(trial.AtUtc.Kind == DateTimeKind.Utc && trial.OutputPath == null, "start evidence malformed");
                    trial.State = "OFF";
                    Require(monitor.Snapshot().State == "ON", "snapshot can mutate monitor");
                    wire.Split("C:\\recordings\\part-2.mkv");
                    wire.State("PAUSED", null);
                    Eventually(() => monitor.Snapshot().State == "PAUSED");
                    wire.State("RESUMED", null);
                    wire.State("STOPPING", null);
                    Eventually(() => monitor.Snapshot().State == "UNKNOWN");
                    // A status query can see OFF before the final event supplies outputPath.
                    Await(monitor.RefreshAsync());
                    wire.State("STOPPED", "C:\\recordings\\final.mkv");
                    Eventually(() => monitor.Snapshot().Detail == "OBS_WEBSOCKET_OUTPUT_STOPPED");
                    Require(monitor.Snapshot().RecordingId == trial.RecordingId, "final event lost earlier trial ID");
                    string[] files = Directory.GetFiles(path, "*.json");
                    Require(files.Length == 1 && Path.GetFileName(files[0]) == trial.RecordingId + ".json", "ledger filename not ID based");
                    var document = ObsRecordingMonitor.Parse(File.ReadAllText(files[0]));
                    Require((string)document["recordingId"] == trial.RecordingId && document["observedStoppedUtc"] != null, "ledger identity/end missing");
                    string json = File.ReadAllText(files[0]);
                    Require(json.Contains("part-2.mkv") && json.Contains("final.mkv") && json.Contains("PAUSED"), "split path or state history missing");
                    Require(!json.Contains("supersecretpassword"), "password persisted");
                }
            }));
            test("OBS ignores uncorrelated responses and fails missing responses as UNKNOWN", () => InLedger(path =>
            {
                var wire = new Wire { Active = true };
                using (var monitor = new ObsRecordingMonitor(path, () => wire, 150))
                {
                    Await(monitor.ConnectAsync("ws://127.0.0.1:4455", ""));
                    wire.Reply = false;
                    Task refresh = monitor.RefreshAsync();
                    wire.Active = false;
                    wire.Respond("unrelated-request");
                    Require(!refresh.IsCompleted && monitor.Snapshot().State == "ON", "unmatched response applied");
                    try { Await(refresh); throw new Exception("missing response accepted"); } catch (AggregateException) { }
                    Require(!monitor.Connected && monitor.Snapshot().State == "UNKNOWN", "timeout was classified off");
                    string json = File.ReadAllText(Directory.GetFiles(path, "*.json")[0]);
                    Require(json.Contains("\"continuityLost\":true"), "gap not saved");
                }
            }));
            test("OBS reconnect creates separate evidence and does not guess old final file", () => InLedger(path =>
            {
                var wires = new Queue<Wire>();
                var first = new Wire { Active = true };
                var second = new Wire { Active = true };
                wires.Enqueue(first); wires.Enqueue(second);
                using (var monitor = new ObsRecordingMonitor(path, () => wires.Dequeue(), 500))
                {
                    Await(monitor.ConnectAsync("ws://127.0.0.1:4455", ""));
                    string oldId = monitor.Snapshot().RecordingId;
                    Await(monitor.ConnectAsync("ws://127.0.0.1:4455", ""));
                    Require(monitor.Snapshot().RecordingId != oldId, "reconnect reused unknown session identity");
                    second.State("STOPPED", "C:\\recordings\\after-gap.mkv");
                    Eventually(() => monitor.Snapshot().State == "OFF");
                    Require(!File.ReadAllText(Path.Combine(path, oldId + ".json")).Contains("after-gap.mkv"), "new file guessed onto old recording");
                }
            }));
            test("OBS malformed status and rejected status are UNKNOWN", () => InLedger(path =>
            {
                var wire = new Wire { Active = true };
                using (var monitor = new ObsRecordingMonitor(path, () => wire, 500))
                {
                    Await(monitor.ConnectAsync("ws://127.0.0.1:4455", ""));
                    wire.Push("{\"op\":5,\"d\":{\"eventType\":\"RecordStateChanged\",\"eventData\":{}}}");
                    Eventually(() => !monitor.Connected);
                    Require(monitor.Snapshot().State == "UNKNOWN", "malformed event became off");
                }
                var rejected = new Wire { BadStatus = true };
                using (var monitor = new ObsRecordingMonitor(path, () => rejected, 500))
                {
                    try { Await(monitor.ConnectAsync("ws://127.0.0.1:4455", "")); throw new Exception("rejected query succeeded"); } catch (AggregateException) { }
                    Require(monitor.Snapshot().State == "UNKNOWN", "rejected status became off");
                }
            }));
        }
    }
}
