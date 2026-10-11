using Copilot.AI;
using Copilot.Audio;
using Copilot.State;
using Copilot.UI;
using Copilot.Vehicle;
using Core = Copilot.Core;
using UnityEngine;

namespace Copilot
{
    /// <summary>
    /// 一键装配（灰盒阶段）：缺什么补什么并接好引用，场景里挂一个即可跑通
    /// 「按键说话/文本 → AI 伴生进程 → 动作 → 车动 + 台词 + 信任变化」闭环。
    /// 正式场景仍建议用 Inspector 手动引用，这里只为降低搭场景成本。
    ///
    /// 前置：AI 伴生进程已启动（<c>dotnet run --project src\Copilot.AIService -- --server</c>）。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class CopilotBootstrap : MonoBehaviour
    {
        [Header("必需")]
        public VehicleController vehicle;

        [Header("连接")]
        public string host = "127.0.0.1";
        public int port = 50051;

        [Header("可选 UI / 音频（留空则自动创建或跳过）")]
        public CopilotConnection connection;
        public ActionExecutor executor;
        public GameStateManager gameState;
        public TtsPlayer ttsPlayer;
        public MicCapture mic;
        public DialogueBubble bubble;
        public HudStatus hud;

        void Awake()
        {
            if (connection == null) connection = GetComponent<CopilotConnection>();
            if (connection == null) connection = gameObject.AddComponent<CopilotConnection>();
            connection.host = host;
            connection.port = port;
            connection.autoConnect = false;

            if (gameState == null) gameState = GetComponent<GameStateManager>();
            if (gameState == null) gameState = gameObject.AddComponent<GameStateManager>();
            gameState.connection = connection;
            gameState.vehicle = vehicle;

            if (executor == null) executor = GetComponent<ActionExecutor>();
            if (executor == null) executor = gameObject.AddComponent<ActionExecutor>();
            executor.connection = connection;
            executor.vehicle = vehicle;

            if (ttsPlayer == null) ttsPlayer = GetComponent<TtsPlayer>();
            if (ttsPlayer == null) ttsPlayer = gameObject.AddComponent<TtsPlayer>();
            ttsPlayer.connection = connection;

            if (mic == null) mic = GetComponent<MicCapture>();
            if (mic == null) mic = gameObject.AddComponent<MicCapture>();
            mic.connection = connection;
            mic.ttsPlayer = ttsPlayer;

            if (hud == null) hud = GetComponent<HudStatus>();
            if (hud == null) hud = gameObject.AddComponent<HudStatus>();
            hud.connection = connection;
            hud.gameState = gameState;
            hud.mic = mic;
            hud.ttsPlayer = ttsPlayer;

            if (bubble != null) bubble.connection = connection;

            connection.Connected += () => Debug.Log("[Copilot] 已连接 AI 服务 " + connection.Endpoint);
            connection.Disconnected += reason => Debug.LogWarning("[Copilot] 断开: " + reason);
            connection.TurnEventReceived += OnTurnEvent;

            // AddComponent 已经先触发过 OnEnable（当时引用还是空的），这里补一次幂等绑定
            executor.Rebind();
            ttsPlayer.Rebind();
            gameState.Rebind();
            hud.Rebind();
            if (bubble != null) bubble.Rebind();
        }

        void Start()
        {
            connection.Connect();
        }

        void OnTurnEvent(Core.TurnEvent evt)
        {
            switch (evt.Kind)
            {
                case Core.TurnEventKind.AsrFinal:
                    Debug.Log("[Copilot] 听到：" + evt.Text);
                    break;
                case Core.TurnEventKind.LlmDelta:
                    Debug.Log("[Copilot] 妹妹：" + evt.Text);
                    break;
                case Core.TurnEventKind.Error:
                    Debug.LogWarning("[Copilot] 错误 " + evt.ErrorCode + "：" + evt.ErrorMessage);
                    break;
            }
        }

        /// <summary>UI 输入框 / 调试用：直接发一条文本指令。</summary>
        public void SendInstruction(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!connection.SendText(text)) Debug.LogWarning("[Copilot] 指令发送失败（服务未连接）");
        }

        /// <summary>场景触发器调用：撞车 / 闯红灯 / 安全到达…（见 <c>Core.TrustState.LocalEvents</c>）。</summary>
        public void ReportLocalEvent(string kind)
        {
            if (gameState != null) gameState.ReportLocalEvent(kind);
        }

        void OnDestroy()
        {
            if (connection != null)
            {
                connection.TurnEventReceived -= OnTurnEvent;
                connection.Disconnect();
            }
        }
    }
}