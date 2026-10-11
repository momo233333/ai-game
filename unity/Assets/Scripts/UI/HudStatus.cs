using Copilot.AI;
using Copilot.Audio;
using Copilot.Core;
using Copilot.State;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.UI
{
    /// <summary>
    /// HUD：连接状态、信任条、情绪、PTT 录音提示、本地事件飘条。
    /// 全部为 UGUI 引用，未配置的元素自动跳过，便于灰盒阶段只挂一部分。
    /// </summary>
    public sealed class HudStatus : MonoBehaviour
    {
        public CopilotConnection connection;
        public GameStateManager gameState;
        public MicCapture mic;
        public TtsPlayer ttsPlayer;

        [Header("绑定（可留空）")]
        public Image connectionDot;
        public Image trustBar;
        public Image recordIndicator;
        public Text trustLabel;
        public Text emotionLabel;
        public Text toastLabel;

        [Header("飘条")]
        public float toastSeconds = 2.5f;

        float _toastTimer;
        bool _bound;

        void OnEnable()
        {
            Rebind();
        }

        /// <summary>幂等绑定全部信号源（AddComponent 会在字段注入前触发 OnEnable）。</summary>
        public void Rebind()
        {
            if (_bound) return;
            _bound = true;
            if (connection != null)
            {
                connection.Connected += OnConnected;
                connection.Disconnected += OnDisconnected;
            }
            if (gameState != null)
            {
                gameState.StateChanged += OnStateChanged;
                gameState.LocalEventApplied += OnLocalEvent;
            }
            if (mic != null) mic.RecordingStarted += OnRecordingStarted;
            if (ttsPlayer != null) ttsPlayer.SpeakingChanged += OnSpeakingChanged;
        }

        void Start()
        {
            Rebind();
            if (gameState != null)
            {
                OnStateChanged(gameState.Trust, gameState.Emotion, 0f);
            }
            OnConnected();
        }

        void OnDisable()
        {
            if (!_bound) return;
            _bound = false;
            if (connection != null)
            {
                connection.Connected -= OnConnected;
                connection.Disconnected -= OnDisconnected;
            }
            if (gameState != null)
            {
                gameState.StateChanged -= OnStateChanged;
                gameState.LocalEventApplied -= OnLocalEvent;
            }
            if (mic != null) mic.RecordingStarted -= OnRecordingStarted;
            if (ttsPlayer != null) ttsPlayer.SpeakingChanged -= OnSpeakingChanged;
        }

        void Update()
        {
            if (_toastTimer <= 0f || toastLabel == null) return;
            _toastTimer -= Time.deltaTime;
            if (_toastTimer <= 0f) toastLabel.text = "";
        }

        void OnConnected()
        {
            if (connectionDot != null) connectionDot.color = new Color(0.35f, 0.9f, 0.45f);
        }

        void OnDisconnected(string reason)
        {
            if (connectionDot != null) connectionDot.color = new Color(0.95f, 0.4f, 0.35f);
            ShowToast("与 AI 服务断开：" + reason);
        }

        void OnStateChanged(float trust, Emotion emotion, float delta)
        {
            if (trustBar != null) trustBar.fillAmount = Mathf.Clamp01(trust / 100f);
            if (trustLabel != null) trustLabel.text = Mathf.RoundToInt(trust) + "/100";
            if (emotionLabel != null) emotionLabel.text = EmotionNames.ToWire(emotion);
            if (delta < -0.01f && toastLabel != null) ShowToast("信任 " + delta.ToString("0"));
        }

        void OnLocalEvent(string kind, string description)
        {
            ShowToast(description);
        }

        void OnRecordingStarted()
        {
            if (recordIndicator != null) recordIndicator.enabled = true;
        }

        void OnSpeakingChanged(bool speaking)
        {
            if (recordIndicator != null && speaking) recordIndicator.enabled = false;
        }

        public void ShowToast(string message)
        {
            if (toastLabel == null) return;
            toastLabel.text = message;
            _toastTimer = toastSeconds;
        }
    }
}
