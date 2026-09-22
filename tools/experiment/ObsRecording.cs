using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VesperLab
{
    public sealed class ObsObservation
    {
        public DateTime AtUtc;
        public string State, RecordingId, OutputPath, Detail;
        internal ObsObservation Copy() { return (ObsObservation)MemberwiseClone(); }
    }

    internal interface IObsWire : IDisposable
    {
        Task ConnectAsync(Uri endpoint, CancellationToken cancel);
        Task SendAsync(string json, CancellationToken cancel);
        Task<string> ReceiveAsync(CancellationToken cancel);
    }

    internal sealed class ObsWebSocketWire : IObsWire
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        public Task ConnectAsync(Uri endpoint, CancellationToken cancel) { return socket.ConnectAsync(endpoint, cancel); }
        public Task SendAsync(string json, CancellationToken cancel)
        {
            return socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, cancel);
        }
        public async Task<string> ReceiveAsync(CancellationToken cancel)
        {
            byte[] buffer = new byte[4096];
            using (var stream = new MemoryStream())
            {
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel).ConfigureAwait(false);
                    if (part.MessageType != WebSocketMessageType.Text) throw new IOException("OBS connection closed or sent unsupported data.");
                    stream.Write(buffer, 0, part.Count);
                    if (stream.Length > 1048576) throw new IOException("OBS message too large.");
                } while (!part.EndOfMessage);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        public void Dispose() { socket.Abort(); socket.Dispose(); }
    }

    // Observer only: the sole request sent after authentication is GetRecordStatus.
    public sealed class ObsRecordingMonitor : IDisposable
    {
        private sealed class Connection
        {
            public IObsWire Wire;
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public readonly Dictionary<string, TaskCompletionSource<bool>> Pending = new Dictionary<string, TaskCompletionSource<bool>>();
            public bool Identified, Closed;
        }
        private sealed class Ledger
        {
            public string recordingId, observedFromUtc, observedStartedUtc, observedStoppedUtc;
            public bool continuityLost;
            public readonly List<string> outputPaths = new List<string>();
            public readonly List<object> observations = new List<object>();
        }
        private readonly object gate = new object();
        private readonly SemaphoreSlim connectGate = new SemaphoreSlim(1, 1), requestGate = new SemaphoreSlim(1, 1);
        private readonly string ledgerDirectory;
        private readonly Func<IObsWire> createWire;
        private readonly int timeoutMs;
        private Connection connection;
        private Ledger ledger;
        private bool disposed;
        private string ledgerError;
        private ObsObservation current = new ObsObservation { AtUtc = DateTime.UtcNow, State = "UNKNOWN", Detail = "OBS not connected" };
        public event Action Changed;

        public ObsRecordingMonitor(string ledgerDirectory) : this(ledgerDirectory, () => new ObsWebSocketWire(), 5000) { }
        internal ObsRecordingMonitor(string ledgerDirectory, Func<IObsWire> createWire, int timeoutMs)
        {
            this.ledgerDirectory = Path.GetFullPath(ledgerDirectory);
            this.createWire = createWire; this.timeoutMs = timeoutMs;
        }
        public bool Connected { get { lock (gate) return connection != null && connection.Identified && !connection.Closed; } }
        public string StatusText { get { lock (gate) return current.State + " — " + current.Detail + (ledgerError == null ? "" : " — " + ledgerError); } }
        public ObsObservation Snapshot() { lock (gate) return current.Copy(); }

        public async Task ConnectAsync(string endpoint, string password)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) || (uri.Scheme != "ws" && uri.Scheme != "wss") || uri.UserInfo.Length != 0)
                throw new ArgumentException("Use an OBS ws:// or wss:// endpoint without embedded credentials.");
            await connectGate.WaitAsync().ConfigureAwait(false);
            Connection next = null;
            try
            {
                Connection previous;
                lock (gate) { if (disposed) throw new ObjectDisposedException("ObsRecordingMonitor"); previous = connection; }
                if (previous != null) Close(previous, "OBS reconnecting; continuity unknown");
                next = new Connection { Wire = createWire() };
                lock (gate) { if (disposed) { next.Wire.Dispose(); throw new ObjectDisposedException("ObsRecordingMonitor"); } connection = next; }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(next.Cancel.Token))
                {
                    deadline.CancelAfter(timeoutMs);
                    await next.Wire.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
                    var hello = Parse(await next.Wire.ReceiveAsync(deadline.Token).ConfigureAwait(false));
                    if (Number(hello, "op") != 0) throw new IOException("OBS did not send Hello.");
                    var data = Object(hello, "d");
                    if (Number(data, "rpcVersion") < 1) throw new IOException("OBS websocket v5 is required.");
                    var identify = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", 64 } };
                    object auth;
                    if (data.TryGetValue("authentication", out auth))
                    {
                        var challenge = auth as Dictionary<string, object>;
                        if (challenge == null) throw new IOException("Malformed OBS authentication challenge.");
                        identify["authentication"] = Authentication(password ?? "", RequiredString(challenge, "salt"), RequiredString(challenge, "challenge"));
                    }
                    await next.Wire.SendAsync(Json(new { op = 1, d = identify }), deadline.Token).ConfigureAwait(false);
                    var identified = Parse(await next.Wire.ReceiveAsync(deadline.Token).ConfigureAwait(false));
                    if (Number(identified, "op") != 2 || Number(Object(identified, "d"), "negotiatedRpcVersion") != 1)
                        throw new IOException("OBS identification failed.");
                }
                lock (gate) { if (next.Closed) throw new IOException("OBS connection ended."); next.Identified = true; }
                // Pump owns all receives, response application and event ordering from this point.
                Task pump = ReceiveLoopAsync(next);
                ObserveFailure(pump);
                await RefreshAsync().ConfigureAwait(false);
            }
            catch
            {
                if (next != null) Close(next, "OBS connection failed; check websocket settings and password");
                throw new IOException("OBS connection failed; check websocket settings and password.");
            }
            finally { connectGate.Release(); }
        }

        public async Task RefreshAsync()
        {
            await requestGate.WaitAsync().ConfigureAwait(false);
            Connection active = null;
            string id = Guid.NewGuid().ToString("N");
            try
            {
                var response = new TaskCompletionSource<bool>();
                lock (gate)
                {
                    active = connection;
                    if (disposed || active == null || active.Closed || !active.Identified) throw new IOException("OBS is not connected.");
                    active.Pending.Add(id, response);
                }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(active.Cancel.Token))
                {
                    deadline.CancelAfter(timeoutMs);
                    using (deadline.Token.Register(() => response.TrySetCanceled()))
                    {
                        await active.Wire.SendAsync(Json(new { op = 6, d = new { requestType = "GetRecordStatus", requestId = id } }), deadline.Token).ConfigureAwait(false);
                        await response.Task.ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                if (active != null) Close(active, "OBS status unavailable; continuity unknown");
                throw new IOException("OBS status unavailable.");
            }
            finally
            {
                if (active != null) lock (gate) active.Pending.Remove(id);
                requestGate.Release();
            }
        }

        private async Task ReceiveLoopAsync(Connection active)
        {
            try
            {
                while (!active.Cancel.IsCancellationRequested)
                {
                    var message = Parse(await active.Wire.ReceiveAsync(active.Cancel.Token).ConfigureAwait(false));
                    lock (gate)
                    {
                        if (active != connection || active.Closed) return;
                        int op = Number(message, "op");
                        var data = Object(message, "d");
                        if (op == 5) ApplyEvent(data);
                        else if (op == 7)
                        {
                            TaskCompletionSource<bool> pending;
                            if (!active.Pending.TryGetValue(RequiredString(data, "requestId"), out pending)) continue;
                            if (RequiredString(data, "requestType") != "GetRecordStatus" || !Boolean(Object(data, "requestStatus"), "result"))
                                throw new IOException("OBS status request failed.");
                            ApplyStatus(Object(data, "responseData"));
                            pending.TrySetResult(true);
                        }
                    }
                    Notify();
                }
            }
            catch { Close(active, "OBS disconnected; continuity unknown"); }
        }

        private void ApplyStatus(Dictionary<string, object> data)
        {
            bool active = Boolean(data, "outputActive"), paused = Boolean(data, "outputPaused");
            Apply(active ? (paused ? "PAUSED" : "ON") : "OFF", null, "GetRecordStatus", false, false);
        }
        private void ApplyEvent(Dictionary<string, object> data)
        {
            string type = RequiredString(data, "eventType");
            if (type != "RecordStateChanged" && type != "RecordFileChanged") return;
            var fields = Object(data, "eventData");
            if (type == "RecordFileChanged")
            {
                Apply(current.State, RequiredString(fields, "newOutputPath"), type, false, false);
                return;
            }
            string state = RequiredString(fields, "outputState"), path = OptionalString(fields, "outputPath");
            string observed;
            switch (state)
            {
                case "OBS_WEBSOCKET_OUTPUT_STARTED":
                case "OBS_WEBSOCKET_OUTPUT_RESUMED": observed = "ON"; break;
                case "OBS_WEBSOCKET_OUTPUT_PAUSED": observed = "PAUSED"; break;
                case "OBS_WEBSOCKET_OUTPUT_STOPPED": observed = "OFF"; break;
                default: observed = "UNKNOWN"; break; // STARTING/STOPPING are transition intervals.
            }
            Apply(observed, path, state, state == "OBS_WEBSOCKET_OUTPUT_STARTED", state == "OBS_WEBSOCKET_OUTPUT_STOPPED");
        }

        // Called under gate. A recording ID names a continuously observed session, never a guessed OBS identity.
        private void Apply(string state, string path, string detail, bool started, bool stopped)
        {
            DateTime now = DateTime.UtcNow;
            if ((state == "ON" || state == "PAUSED") && (ledger == null || current.State == "OFF"))
                ledger = new Ledger { recordingId = "obs-" + Guid.NewGuid().ToString("N"), observedFromUtc = now.ToString("o") };
            if (ledger != null && started) ledger.observedStartedUtc = now.ToString("o");
            if (ledger != null && stopped) ledger.observedStoppedUtc = now.ToString("o");
            if (ledger != null && !String.IsNullOrWhiteSpace(path) && !ledger.outputPaths.Contains(path)) ledger.outputPaths.Add(path);
            current = new ObsObservation { AtUtc = now, State = state, RecordingId = ledger == null ? null : ledger.recordingId,
                OutputPath = path ?? (ledger != null && ledger.outputPaths.Count > 0 ? ledger.outputPaths[ledger.outputPaths.Count - 1] : null), Detail = detail };
            if (ledger != null)
            {
                ledger.observations.Add(new { atUtc = now.ToString("o"), state = state, outputPath = path, detail = detail });
                SaveLedger();
                // OFF queries can arrive before STOPPED carries the final file path.
                if (stopped) ledger = null;
            }
        }
        private void SaveLedger()
        {
            try
            {
                Directory.CreateDirectory(ledgerDirectory);
                string destination = Path.Combine(ledgerDirectory, ledger.recordingId + ".json");
                string temporary = destination + ".tmp";
                File.WriteAllText(temporary, Json(ledger), new UTF8Encoding(false));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                ledgerError = null;
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException)) throw;
                ledgerError = "recording ledger could not be saved";
            }
        }
        private void Close(Connection active, string detail)
        {
            List<TaskCompletionSource<bool>> pending;
            lock (gate)
            {
                if (active.Closed) return;
                active.Closed = true;
                pending = new List<TaskCompletionSource<bool>>(active.Pending.Values);
                active.Pending.Clear();
                if (connection == active)
                {
                    if (ledger != null) ledger.continuityLost = true;
                    Apply("UNKNOWN", null, detail, false, false);
                    ledger = null;
                    current.RecordingId = null; current.OutputPath = null;
                }
            }
            active.Cancel.Cancel(); active.Wire.Dispose();
            foreach (var request in pending) request.TrySetCanceled();
            Notify();
        }
        private void Notify()
        {
            var handler = Changed;
            if (handler == null) return;
            foreach (Action callback in handler.GetInvocationList())
                try { callback(); } catch { /* UI lifetime must not terminate transport. */ }
        }
        private static void ObserveFailure(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        public void Dispose()
        {
            Connection active;
            lock (gate) { if (disposed) return; disposed = true; active = connection; }
            if (active != null) Close(active, "OBS monitor closed; continuity unknown");
        }
        public static string Classify(IEnumerable<ObsObservation> observations)
        {
            bool any = false, on = false, off = false, paused = false;
            foreach (var observation in observations)
            {
                if (observation == null || (observation.State != "ON" && observation.State != "OFF" && observation.State != "PAUSED")) return "UNKNOWN";
                any = true; on |= observation.State == "ON"; off |= observation.State == "OFF"; paused |= observation.State == "PAUSED";
            }
            if (!any) return "UNKNOWN";
            return paused || (on && off) ? "MIXED" : (on ? "ON" : "OFF");
        }
        internal static string Authentication(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }
        internal static Dictionary<string, object> Parse(string json) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
        internal static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
        private static Dictionary<string, object> Object(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || !(value is Dictionary<string, object>)) throw new IOException("Malformed OBS message.");
            return (Dictionary<string, object>)value;
        }
        private static int Number(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || !(value is int)) throw new IOException("Malformed OBS number.");
            return (int)value;
        }
        private static bool Boolean(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || !(value is bool)) throw new IOException("Malformed OBS boolean.");
            return (bool)value;
        }
        private static string OptionalString(Dictionary<string, object> data, string key)
        {
            object value; return data.TryGetValue(key, out value) ? value as string : null;
        }
        private static string RequiredString(Dictionary<string, object> data, string key)
        {
            string value = OptionalString(data, key);
            if (value == null) throw new IOException("Malformed OBS string.");
            return value;
        }
    }
}
