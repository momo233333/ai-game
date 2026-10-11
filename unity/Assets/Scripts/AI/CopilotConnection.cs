using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Copilot.Client;
using UnityEngine;
using Core = Copilot.Core;

namespace Copilot.AI
{
    /// <summary>
    /// AI 伴生进程连接（IT方案1.md V2.0 §4）。
    ///
    /// 传输全部交给共享库 <see cref="CopilotClient"/>（**不依赖 UnityEngine**，已在
    /// <c>tests/Copilot.Tests/ClientTests.cs</c> 用真 gRPC 服务端验证过）；
    /// 本组件只做两件 Unity 侧特有的事：
    /// ① 把后台线程收到的事件**排队到主线程**再派发（Unity API 只能在主线程调）；
    /// ② 用 <see cref="Update"/> 每帧限量消费队列。
    /// </summary>
    public sealed class CopilotConnection : MonoBehaviour
    {
        [Header("服务端")]
        public string host = "127.0.0.1";
        public int port = 50051;
        public bool autoConnect = true;

        [Header("心跳 / 重连")]
        [Tooltip("健康检查间隔（秒）。服务端没起来时会按这个间隔自动重试。")]
        public float heartbeatSeconds = 3f;

        [Tooltip("每帧最多派发的事件数，防止一帧涌入过多事件卡住主线程")]
        public int dispatchBudget = 256;

        readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        CopilotClient _client;
        CancellationTokenSource _cts;

        /// <summary>已与 AI 服务建立连接（首次 Ping 成功）。</summary>
        public bool IsConnected { get; private set; }

        public string Endpoint
        {
            get { return "http://" + host + ":" + port; }
        }

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<Core.TurnEvent> TurnEventReceived;
        public event Action TurnCompleted;

        void Start()
        {
            if (autoConnect) Connect();
        }

        /// <summary>建立连接并启动心跳（幂等）。</summary>
        public void Connect()
        {
            if (_client != null) return;

            _cts = new CancellationTokenSource();
            _client = new CopilotClient(host, port);

            var client = _client;
            var token = _cts.Token;
            int intervalMs = Mathf.Max(500, Mathf.RoundToInt(heartbeatSeconds * 1000f));

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await client.HealthAsync(token);
                        if (!IsConnected) Enqueue(() => SetConnected(true));
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (IsConnected) Enqueue(() => SetConnected(false, ex.Message));
                    }

                    try { await Task.Delay(intervalMs, token); }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        /// <summary>断开并释放（重连需再调 <see cref="Connect"/>）。</summary>
        public void Disconnect()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
            IsConnected = false;
        }

        /// <summary>文本回合（调试通道，与语音链路等价）。</summary>
        public bool SendText(string text)
        {
            if (_client == null || string.IsNullOrEmpty(text)) return false;

            var client = _client;
            var token = _cts.Token;
            string payload = text;

            Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in client.TextTurnAsync(payload, token))
                    {
                        var captured = e;
                        Enqueue(() => TurnEventReceived?.Invoke(captured));
                    }
                    Enqueue(() => TurnCompleted?.Invoke());
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Enqueue(() => Disconnected?.Invoke(ex.Message));
                }
            }, token);

            return true;
        }

        /// <summary>
        /// 提交一段**已经录完的** 16kHz 单声道 PCM16 作为一次语音回合。
        /// 录制策略（按键说话 / 免提）留给 <c>MicCapture</c>，这里只负责上行与收事件。
        /// </summary>
        public bool SendVoice(byte[] pcm16)
        {
            if (_client == null || pcm16 == null || pcm16.Length == 0) return false;

            var client = _client;
            var token = _cts.Token;
            var audio = pcm16;

            Task.Run(async () =>
            {
                try
                {
                    using (var session = client.StartVoiceTurn(token))
                    {
                        // 16kHz 单声道 → 100ms/块（3200 字节），最后一块标记结束
                        const int blockBytes = 3200;
                        for (int offset = 0; offset < audio.Length; offset += blockBytes)
                        {
                            int length = Math.Min(blockBytes, audio.Length - offset);
                            var slice = new byte[length];
                            Array.Copy(audio, offset, slice, 0, length);
                            bool last = offset + length >= audio.Length;
                            await session.WriteAsync(slice, last, token);
                        }
                        await session.CompleteRequestAsync();

                        await foreach (var e in session.ReadEventsAsync(token))
                        {
                            var captured = e;
                            Enqueue(() => TurnEventReceived?.Invoke(captured));
                        }
                    }
                    Enqueue(() => TurnCompleted?.Invoke());
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Enqueue(() => Disconnected?.Invoke(ex.Message));
                }
            }, token);

            return true;
        }

        /// <summary>回推权威游戏状态（信任/情绪/遥测）。</summary>
        public bool SyncState(Core.GameStateSnapshot state)
        {
            if (_client == null || state == null) return false;

            var client = _client;
            var token = _cts.Token;
            Task.Run(async () =>
            {
                try { await client.SyncGameStateAsync(state, token); }
                catch (OperationCanceledException) { }
                catch (Exception) { /* 状态同步失败不致命，下一拍会再推 */ }
            }, token);

            return true;
        }

        void Update()
        {
            int budget = dispatchBudget;
            Action action;
            while (budget-- > 0 && _mainThread.TryDequeue(out action))
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
            _mainThread.Enqueue(action);
        }

        void SetConnected(bool connected, string reason = null)
        {
            IsConnected = connected;
            if (connected)
            {
                if (Connected != null) Connected();
            }
            else if (Disconnected != null)
            {
                Disconnected(reason ?? "连接断开");
            }
        }

        void OnDestroy()
        {
            Disconnect();
        }
    }
}