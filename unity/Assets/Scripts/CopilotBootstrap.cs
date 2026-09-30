using Copilot.AI;
using Copilot.Audio;
using Copilot.State;
using Copilot.UI;
using Copilot.Vehicle;
using UnityEngine;

namespace Copilot
{
    /// <summary>
    /// 一键装配（灰盒阶段）：缺什么补什么并接好引用，场景里挂一个即可跑通
    /// 「文本/语音 → AI 服务 → 动作 → 车动 + 台词 + 信任变化」闭环。
    /// 正式场景仍建议用 Inspector 手动引用，这里只为降低搭场景成本。
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
        public AIClient client;
        public ActionExecutor executor;
        public GameStateManager gameState;
        public TtsPlayer ttsPlayer;
        public MicCapture mic;
        public DialogueBubble bubble;
        public HudStatus hud;

        void Awake()
        {
            if (client == null) client = GetComponent<AIClient>();
            if (client == null) client = gameObject.AddComponent<AIClient>();
            client.host = host;
            client.port = port;
            client.autoConnect = false;

            if (gameState == null) gameState = GetComponent<GameStateManager>();
            if (gameState == null) gameState = gameObject.AddComponent<GameStateManager>();
            gameState.client = client;
            gameState.vehicle = vehicle;

            if (executor == null) executor = GetComponent<ActionExecutor>();
            if (executor == null) executor = gameObject.AddComponent<ActionExecutor>();
            executor.client = client;
            executor.vehicle = vehicle;

            if (ttsPlayer == null) ttsPlayer = GetComponent<TtsPlayer>();
            if (ttsPlayer == null) ttsPlayer = gameObject.AddComponent<TtsPlayer>();
            ttsPlayer.client = client;

            if (mic == null) mic = GetComponent<MicCapture>();
            if (mic == null) mic = gameObject.AddComponent<MicCapture>();
            mic.client = client;
            mic.ttsPlayer = ttsPlayer;

            if (hud == null) hud = GetComponent<HudStatus>();
            if (hud == null) hud = gameObject.AddComponent<HudStatus>();
            hud.client = client;
            hud.gameState = gameState;
            hud.mic = mic;
            hud.ttsPlayer = ttsPlayer;

            client.Connected += () => Debug.Log("[Copilot] 已连接 AI 服务 " + host + ":" + port);
            client.Disconnected += reason => Debug.LogWarning("[Copilot] 断开: " + reason);

            // AddComponent 已经先触发过 OnEnable（当时引用还是空的），这里补一次幂等绑定
            executor.Rebind();
            ttsPlayer.Rebind();
            hud.Rebind();
        }

        void Start()
        {
            client.Connect();
        }

        /// <summary>UI 输入框 / 调试用：直接发一条文本指令。</summary>
        public void SendInstruction(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!client.SendText(text)) Debug.LogWarning("[Copilot] 指令发送失败（服务未连接）");
        }

        /// <summary>场景触发器调用：撞车 / 闯红灯 / 安全到达…</summary>
        public void ReportLocalEvent(string kind)
        {
            if (gameState != null) gameState.ReportLocalEvent(kind);
        }

        void OnDestroy()
        {
            if (client != null) client.Disconnect();
        }
    }
}
