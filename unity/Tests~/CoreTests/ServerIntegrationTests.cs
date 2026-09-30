using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Copilot.Core;

namespace Copilot.Tests
{
    /// <summary>
    /// 起真实 python server.py（JSON-TCP），用 Core 层客户端跑一轮闭环：
    /// ping → text_turn → drive_action/llm_delta/tts_audio/state_delta → local_event → state_query。
    /// </summary>
    public sealed class ServerFixture : IDisposable
    {
        public Process Process;
        public int Port;
        public string ServiceDir;

        public static string FindRepoRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "ai-service", "server.py"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            return null;
        }

        public static bool TryStart(out ServerFixture fixture, out string error)
        {
            fixture = null;
            error = null;

            string root = FindRepoRoot();
            if (root == null) { error = "repo root not found (ai-service/server.py missing)"; return false; }

            string serviceDir = Path.Combine(root, "ai-service");
            int port = FreePort();

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"server.py --transport json --port {port}",
                WorkingDirectory = serviceDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
            psi.Environment["PYTHONUNBUFFERED"] = "1";

            Process process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                error = "cannot start python: " + ex.Message;
                return false;
            }
            if (process == null) { error = "python process not started"; return false; }

            var stdout = new List<string>();
            var stdoutLock = new object();
            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (stdoutLock) stdout.Add(e.Data);
            };
            process.ErrorDataReceived += (s, e) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    lock (stdoutLock) error = "server exited early: " + string.Join(" | ", stdout.TakeLast(5));
                    try { process.Kill(); } catch (Exception) { }
                    return false;
                }
                bool ready;
                lock (stdoutLock) ready = stdout.Any(l => l.Contains("JSON-TCP listening"));
                if (ready) break;
                Thread.Sleep(100);
            }

            bool started;
            lock (stdoutLock) started = stdout.Any(l => l.Contains("JSON-TCP listening"));
            if (!started)
            {
                error = "server did not become ready in 30s";
                try { process.Kill(); } catch (Exception) { }
                return false;
            }

            fixture = new ServerFixture { Process = process, Port = port, ServiceDir = serviceDir };
            return true;
        }

        static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            if (Process == null) return;
            try
            {
                if (!Process.HasExited) Process.Kill();
                Process.WaitForExit(3000);
            }
            catch (Exception) { }
            finally
            {
                Process.Dispose();
                Process = null;
            }
        }
    }

    public static class ServerIntegrationTests
    {
        static ServerFixture _fixture;
        static string _skipReason;

        /// <summary>CI 用：python 服务起不来时直接判失败，而不是静默跳过。</summary>
        public static bool RequireServer;

        static bool Enabled
        {
            get { return Environment.GetEnvironmentVariable("COPILOT_SKIP_SERVER") != "1"; }
        }

        static void EnsureFixture()
        {
            if (!Enabled) { _skipReason = "COPILOT_SKIP_SERVER=1"; return; }
            if (_skipReason != null)
            {
                if (RequireServer) throw new AssertFailedException("server unavailable: " + _skipReason);
                return;
            }
            if (_fixture != null) return;
            if (!ServerFixture.TryStart(out _fixture, out _skipReason))
            {
                Console.WriteLine("  (skip) python server unavailable: " + _skipReason);
                if (RequireServer) throw new AssertFailedException("server unavailable: " + _skipReason);
            }
        }

        static JsonTcpSession Session()
        {
            EnsureFixture();
            if (_skipReason != null) throw new AssertFailedException("server unavailable: " + _skipReason);

            var session = new JsonTcpSession { ConnectTimeoutMs = 2000, AutoReconnect = false };
            var connected = new ManualResetEventSlim(false);
            Action onConnected = () => connected.Set();
            session.Connected += onConnected;
            try
            {
                session.Connect("127.0.0.1", _fixture.Port);
                if (!connected.Wait(5000)) throw new AssertFailedException("cannot connect to python server");
            }
            finally
            {
                session.Connected -= onConnected;
            }
            return session;
        }

        static List<TurnEvent> RunTurn(JsonTcpSession session, string text, int timeoutMs = 20000)
        {
            var events = new List<TurnEvent>();
            var done = new ManualResetEventSlim(false);
            Action<TurnEvent> onEvent = evt => { lock (events) events.Add(evt); };
            Action onEnd = () => done.Set();
            session.TurnEventReceived += onEvent;
            session.TurnCompleted += onEnd;
            try
            {
                if (!session.SendTextTurn(text)) throw new AssertFailedException("send text_turn failed");
                if (!done.Wait(timeoutMs))
                    throw new AssertFailedException("turn did not complete in " + timeoutMs + "ms");
            }
            finally
            {
                session.TurnEventReceived -= onEvent;
                session.TurnCompleted -= onEnd;
            }
            return events;
        }

        static List<KeyValuePair<string, Dictionary<string, object>>> RunRequest(
            JsonTcpSession session, Dictionary<string, object> request, int timeoutMs = 8000)
        {
            var frames = new List<KeyValuePair<string, Dictionary<string, object>>>();
            var done = new ManualResetEventSlim(false);
            Action<string, Dictionary<string, object>> onFrame = (kind, data) =>
            {
                lock (frames) frames.Add(new KeyValuePair<string, Dictionary<string, object>>(kind, data));
                if (kind == "end") done.Set();
            };
            session.FrameReceived += onFrame;
            try
            {
                if (!session.Send(request)) throw new AssertFailedException("send failed: " + request["type"]);
                if (!done.Wait(timeoutMs)) throw new AssertFailedException("no end frame within " + timeoutMs + "ms");
            }
            finally
            {
                session.FrameReceived -= onFrame;
            }
            return frames;
        }

        [Test]
        public static void Ping_ReportsHealth()
        {
            EnsureFixture();
            if (_skipReason != null) return;
            using (var session = Session())
            {
                var frames = RunRequest(session, new Dictionary<string, object> { { "type", "ping" } });
                var health = frames.First(f => f.Key == "health").Value;
                Assert.True(SimpleJson.GetBool(health, "ok"), "health.ok");
                Assert.True(SimpleJson.GetString(health, "llm_backend").Length > 0, "llm_backend 非空");
            }
        }

        [Test]
        public static void TextTurn_EmitsActionThenDialogueThenState()
        {
            EnsureFixture();
            if (_skipReason != null) return;
            using (var session = Session())
            {
                var events = RunTurn(session, "前面路口左转");
                var kinds = events.Select(e => e.Kind).ToList();

                Assert.True(kinds.Contains("drive_action"), "包含 drive_action: " + string.Join(",", kinds));
                Assert.True(kinds.Contains("llm_delta"), "包含 llm_delta");
                Assert.True(kinds.Contains("tts_audio"), "包含 tts_audio");
                Assert.True(kinds.Contains("state_delta"), "包含 state_delta");

                int actionIndex = kinds.IndexOf("drive_action");
                int replyIndex = kinds.FindIndex(k => k == "llm_delta");
                int stateIndex = kinds.LastIndexOf("state_delta");
                Assert.True(actionIndex < replyIndex, "动作先于台词（方案 §3.2）");
                Assert.True(replyIndex < stateIndex, "状态最后结算");

                var action = events[actionIndex].ToDriveAction();
                Assert.True(Intents.IsAllowed(action.Intent), "intent 在白名单内: " + action.Intent);
                Assert.True(action.HoldMs >= 0 && action.HoldMs <= ActionExecutorCore.MaxHoldMs, "hold_ms 合法");

                var tts = events.First(e => e.Kind == "tts_audio");
                Assert.True(tts.Data.ContainsKey("pcm16"), "tts_audio 带 pcm16 字段");
                Assert.True(SimpleJson.GetString(tts.Data, "text").Length > 0, "tts_audio 带字幕文本");
                byte[] raw = PcmAudio.DecodeBytes(tts.Data["pcm16"]);
                if (raw != null && raw.Length > 0)
                {
                    var chunk = PcmAudio.FromEvent(tts);
                    Assert.True(chunk != null && chunk.Samples.Length == raw.Length / 2, "有音频时可解码出采样");
                }

                var delta = events.Last(e => e.Kind == "state_delta");
                Assert.True(SimpleJson.GetFloat(delta.Data, "trust") >= 0f, "trust >= 0");
            }
        }

        [Test]
        public static void SensitiveInput_IsBlockedBeforeLlm()
        {
            EnsureFixture();
            if (_skipReason != null) return;
            using (var session = Session())
            {
                var events = RunTurn(session, "去死吧你这个混蛋");
                var kinds = events.Select(e => e.Kind).ToList();
                Assert.True(kinds.Contains("drive_action"), "拦截也下发动作: " + string.Join(",", kinds));

                var action = events.First(e => e.Kind == "drive_action").ToDriveAction();
                Assert.Equal(Intents.Ignore, action.Intent);

                var delta = events.Last(e => e.Kind == "state_delta");
                Assert.True(SimpleJson.GetFloat(delta.Data, "trust_delta") < 0f, "辱骂扣信任");
            }
        }

        [Test]
        public static void LocalEvent_CollisionDropsTrustBy12()
        {
            EnsureFixture();
            if (_skipReason != null) return;
            using (var session = Session())
            {
                var events = new List<TurnEvent>();
                var done = new ManualResetEventSlim(false);
                Action<TurnEvent> onEvent = evt => { lock (events) events.Add(evt); };
                Action onEnd = () => done.Set();
                session.TurnEventReceived += onEvent;
                session.TurnCompleted += onEnd;
                try
                {
                    Assert.True(session.SendLocalEvent("collision"), "send local_event");
                    Assert.True(done.Wait(5000), "state_delta within 5s");
                }
                finally
                {
                    session.TurnEventReceived -= onEvent;
                    session.TurnCompleted -= onEnd;
                }
                var delta = events.Last(e => e.Kind == "state_delta");
                Assert.Nearly(-12.0, SimpleJson.GetFloat(delta.Data, "trust_delta"), 0.01, "撞车 -12");
            }
        }

        [Test]
        public static void StateQuery_ReturnsSnapshot()
        {
            EnsureFixture();
            if (_skipReason != null) return;
            using (var session = Session())
            {
                var frames = RunRequest(session, new Dictionary<string, object> { { "type", "state_query" } });
                var state = frames.First(f => f.Key == "state").Value;
                Assert.True(SimpleJson.GetDouble(state, "trust") >= 0, "trust 有值");
                var view = new StateView();
                view.LoadSnapshot(state);
                Assert.Equal(EmotionUtil.FromName(SimpleJson.GetString(state, "emotion")), view.Emotion);
            }
        }

        public static void Shutdown()
        {
            if (_fixture != null)
            {
                _fixture.Dispose();
                _fixture = null;
            }
        }
    }
}
