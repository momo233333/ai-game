using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Copilot.Core
{
    /// <summary>
    /// JSON-TCP 会话客户端（方案 §1.2 AIClient 的传输核心）：
    /// 换行分隔 JSON、请求串行化（服务端每请求后回 {"kind":"end"}）、断线自动重连。
    /// 线程安全：Send 任意线程可调；事件在后台读线程触发，Unity 侧需自行 marshal 到主线程。
    /// </summary>
    public sealed class JsonTcpSession : IDisposable
    {
        static readonly HashSet<string> EventKinds = new HashSet<string>
        {
            "asr_partial", "asr_final", "llm_delta", "tts_audio",
            "drive_action", "state_delta", "error",
        };

        readonly object _sendLock = new object();
        readonly object _stateLock = new object();

        TcpClient _client;
        StreamWriter _writer;
        Thread _readThread;
        volatile bool _disposed;
        volatile bool _connected;
        int _requestInFlight;
        int _reconnectDelayMs = 500;

        public string Host { get; private set; }
        public int Port { get; private set; }
        public bool AutoReconnect = true;
        public int ConnectTimeoutMs = 3000;

        public bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>任意一帧（含 health/ack/state/end 等控制帧）。</summary>
        public event Action<string, Dictionary<string, object>> FrameReceived;

        /// <summary>一轮业务事件（drive_action / llm_delta / tts_audio / state_delta / …）。</summary>
        public event Action<TurnEvent> TurnEventReceived;

        /// <summary>一轮结束（收到 {"kind":"end"}）。</summary>
        public event Action TurnCompleted;

        public event Action Connected;
        public event Action<Exception> Disconnected;

        public void Connect(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            lock (_stateLock)
            {
                Host = host;
                Port = port;
            }
            StartReader();
        }

        /// <summary>发送一个请求；若上一请求尚未收到 end，先等待其完成（串行化）。</summary>
        public bool Send(Dictionary<string, object> request, int waitMs = 2000)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!WaitIdle(waitMs)) return false;

            Interlocked.Exchange(ref _requestInFlight, 1);
            try
            {
                lock (_sendLock)
                {
                    if (_writer == null) return false;
                    string line = SimpleJson.Write(request) + "\n";
                    _writer.Write(line);
                    _writer.Flush();
                    return true;
                }
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref _requestInFlight, 0);
                return false;
            }
        }

        public bool SendTextTurn(string text)
        {
            return Send(new Dictionary<string, object>
            {
                { "type", "text_turn" },
                { "text", text ?? "" },
            });
        }

        public bool Ping()
        {
            return Send(new Dictionary<string, object> { { "type", "ping" } });
        }

        public bool SyncState(StateView state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return Send(new Dictionary<string, object>
            {
                { "type", "sync_state" },
                { "state", state.ToSyncPayload() },
            });
        }

        public bool SendLocalEvent(string kind)
        {
            return Send(new Dictionary<string, object>
            {
                { "type", "local_event" },
                { "event", kind ?? "" },
            });
        }

        public bool QueryState()
        {
            return Send(new Dictionary<string, object> { { "type", "state_query" } });
        }

        bool WaitIdle(int waitMs)
        {
            int waited = 0;
            while (Volatile.Read(ref _requestInFlight) != 0)
            {
                if (waited >= waitMs) return false;
                Thread.Sleep(10);
                waited += 10;
            }
            return true;
        }

        void StartReader()
        {
            if (_readThread != null && _readThread.IsAlive) return;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "json-tcp-reader" };
            _readThread.Start();
        }

        void ReadLoop()
        {
            while (!_disposed)
            {
                TcpClient client = null;
                StreamReader reader = null;
                StreamWriter writer = null;
                try
                {
                    client = new TcpClient();
                    client.NoDelay = true;
                    client.SendTimeout = 5000;
                    client.ReceiveTimeout = 0;
                    if (!client.ConnectAsync(Host, Port).Wait(ConnectTimeoutMs))
                        throw new IOException("connect timeout");

                    var stream = client.GetStream();
                    reader = new StreamReader(stream, new UTF8Encoding(false));
                    writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };

                    lock (_stateLock)
                    {
                        _client = client;
                        _writer = writer;
                    }
                    _connected = true;
                    _reconnectDelayMs = 500;
                    Interlocked.Exchange(ref _requestInFlight, 0);
                    if (Connected != null) Connected();

                    string line;
                    while (!_disposed && (line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        DispatchLine(line);
                    }
                    throw new IOException("connection closed by peer");
                }
                catch (Exception ex)
                {
                    if (_disposed) break;
                    bool wasConnected = _connected;
                    _connected = false;
                    lock (_stateLock)
                    {
                        _writer = null;
                        if (_client != null)
                        {
                            try { _client.Close(); } catch (Exception) { }
                            _client = null;
                        }
                    }
                    Interlocked.Exchange(ref _requestInFlight, 0);
                    if (wasConnected && Disconnected != null) Disconnected(ex);
                }
                finally
                {
                    if (writer != null) { try { writer.Dispose(); } catch (Exception) { } }
                    if (reader != null) { try { reader.Dispose(); } catch (Exception) { } }
                    if (client != null) { try { client.Close(); } catch (Exception) { } }
                }

                if (_disposed || !AutoReconnect) break;
                Thread.Sleep(_reconnectDelayMs);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 5000);
            }
        }

        void DispatchLine(string line)
        {
            TurnEvent evt;
            if (!TurnEvent.TryParse(line, out evt))
            {
                object root;
                if (SimpleJson.TryParse(line, out root))
                {
                    var dict = SimpleJson.AsDict(root);
                    if (dict != null)
                    {
                        string kind = SimpleJson.GetString(dict, "kind", "unknown");
                        var data = SimpleJson.AsDict(dict.ContainsKey("data") ? dict["data"] : null)
                                   ?? new Dictionary<string, object>();
                        RaiseFrame(kind, data);
                    }
                }
                return;
            }

            if (evt.Kind == "end")
            {
                Interlocked.Exchange(ref _requestInFlight, 0);
                RaiseFrame("end", evt.Data);
                if (TurnCompleted != null) TurnCompleted();
                return;
            }

            if (EventKinds.Contains(evt.Kind))
            {
                if (TurnEventReceived != null) TurnEventReceived(evt);
            }
            RaiseFrame(evt.Kind, evt.Data);
        }

        void RaiseFrame(string kind, Dictionary<string, object> data)
        {
            var handler = FrameReceived;
            if (handler != null) handler(kind, data);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connected = false;
            lock (_stateLock)
            {
                _writer = null;
                if (_client != null)
                {
                    try { _client.Close(); } catch (Exception) { }
                    _client = null;
                }
            }
            if (_readThread != null && _readThread.IsAlive && _readThread != Thread.CurrentThread)
            {
                _readThread.Join(500);
            }
        }
    }
}
