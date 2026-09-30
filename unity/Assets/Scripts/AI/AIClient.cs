using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Copilot.AI
{
    /// <summary>
    /// AI 服务客户端（方案 §1.2 AIClient）：JSON-TCP 传输，后台线程收发、主线程分发。
    /// 断线重连、超时、请求串行化由 Copilot.Core.JsonTcpSession 负责。
    /// </summary>
    public sealed class AIClient : MonoBehaviour
    {
        public string host = "127.0.0.1";
        public int port = 50051;
        public bool autoConnect = true;
        [Tooltip("断线后自动重连（指数退避）")]
        public bool autoReconnect = true;

        readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        Copilot.Core.JsonTcpSession _session;

        /// <summary>已连上 AI 服务。</summary>
        public event Action Connected;

        /// <summary>断开（参数为原因文本）。</summary>
        public event Action<string> Disconnected;

        /// <summary>一轮业务事件（drive_action / llm_delta / tts_audio / state_delta / …）。</summary>
        public event Action<Copilot.Core.TurnEvent> TurnEventReceived;

        /// <summary>控制帧（health / ack / state / end / error）。</summary>
        public event Action<string, Dictionary<string, object>> FrameReceived;

        public event Action TurnCompleted;

        public bool IsConnected
        {
            get { return _session != null && _session.IsConnected; }
        }

        void Start()
        {
            if (autoConnect) Connect();
        }

        public void Connect()
        {
            if (_session != null && _session.IsConnected) return;
            DisposeSession();

            var session = new Copilot.Core.JsonTcpSession { AutoReconnect = autoReconnect };
            session.Connected += () => Enqueue(() => { if (Connected != null) Connected(); });
            session.Disconnected += ex => Enqueue(() => { if (Disconnected != null) Disconnected(ex.Message); });
            session.TurnEventReceived += evt => Enqueue(() => { if (TurnEventReceived != null) TurnEventReceived(evt); });
            session.TurnCompleted += () => Enqueue(() => { if (TurnCompleted != null) TurnCompleted(); });
            session.FrameReceived += (kind, data) => Enqueue(() => { if (FrameReceived != null) FrameReceived(kind, data); });

            _session = session;
            session.Connect(host, port);
        }

        public void Disconnect()
        {
            DisposeSession();
        }

        public bool SendText(string text)
        {
            return _session != null && _session.SendTextTurn(text);
        }

        public bool SyncState(Copilot.Core.StateView state)
        {
            return _session != null && _session.SyncState(state);
        }

        public bool SendLocalEvent(string kind)
        {
            return _session != null && _session.SendLocalEvent(kind);
        }

        public bool QueryState()
        {
            return _session != null && _session.QueryState();
        }

        public bool Ping()
        {
            return _session != null && _session.Ping();
        }

        /// <summary>低层入口：发送任意协议请求（audio_turn 等复合载荷走这里）。</summary>
        public bool Send(Dictionary<string, object> request)
        {
            return _session != null && _session.Send(request);
        }

        void Update()
        {
            Action action;
            int budget = 256;
            while (budget-- > 0 && _mainThreadQueue.TryDequeue(out action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        void Enqueue(Action action)
        {
            _mainThreadQueue.Enqueue(action);
        }

        void OnDestroy()
        {
            DisposeSession();
        }

        void DisposeSession()
        {
            if (_session == null) return;
            var session = _session;
            _session = null;
            session.Dispose();
        }
    }
}
