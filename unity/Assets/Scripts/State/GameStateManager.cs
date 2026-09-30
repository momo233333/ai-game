using System;
using Copilot.AI;
using Copilot.Core;
using Copilot.Vehicle;
using UnityEngine;

namespace Copilot.State
{
    /// <summary>
    /// 信任/情绪/遥测的唯一表现层数据源（方案 §5.2）：事件驱动，UI 订阅刷新。
    /// 服务端 StateDelta 权威；本地物理事件先上报服务端，断线时用 StateView 本地表兜底。
    /// </summary>
    public sealed class GameStateManager : MonoBehaviour
    {
        public AIClient client;
        public VehicleController vehicle;

        [Header("同步")]
        [Tooltip("向服务端推送 telemetry 的周期（秒）")]
        public float syncInterval = 1.0f;
        [Tooltip("场景/关卡标识，随 sync_state 上报")]
        public string levelId = "level_01";
        public string sceneId = "city_01";
        public string weather = "晴";
        public string timeOfDay = "傍晚";

        readonly StateView _view = new StateView();
        float _syncTimer;

        public StateView View
        {
            get { return _view; }
        }

        public float Trust
        {
            get { return _view.Trust; }
        }

        public Emotion Emotion
        {
            get { return _view.Emotion; }
        }

        public event Action<float, Emotion, float> StateChanged;
        public event Action<string, string> LocalEventApplied;

        void Start()
        {
            _view.LevelId = levelId;
            _view.SceneId = sceneId;
            _view.Weather = weather;
            _view.TimeOfDay = timeOfDay;
            _view.StateChanged += OnViewChanged;
            _view.LocalEventApplied += OnLocalEventApplied;

            if (client != null)
            {
                client.TurnEventReceived += HandleTurnEvent;
                client.FrameReceived += HandleFrame;
                client.Connected += HandleConnected;
            }
        }

        void OnDestroy()
        {
            _view.StateChanged -= OnViewChanged;
            _view.LocalEventApplied -= OnLocalEventApplied;
            if (client != null)
            {
                client.TurnEventReceived -= HandleTurnEvent;
                client.FrameReceived -= HandleFrame;
                client.Connected -= HandleConnected;
            }
        }

        void HandleConnected()
        {
            PushTelemetry();
            if (client != null) client.SyncState(_view);
            if (client != null) client.QueryState();
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null || evt.Kind != "state_delta") return;
            _view.ApplyServerDelta(
                Copilot.Core.SimpleJson.GetFloat(evt.Data, "trust", _view.Trust),
                EmotionUtil.FromName(Copilot.Core.SimpleJson.GetString(evt.Data, "emotion", "CALM")),
                Copilot.Core.SimpleJson.GetFloat(evt.Data, "trust_delta"));
        }

        void HandleFrame(string kind, System.Collections.Generic.Dictionary<string, object> data)
        {
            if (kind != "state") return;
            _view.LoadSnapshot(data);
        }

        void Update()
        {
            _syncTimer += Time.deltaTime;
            if (_syncTimer < syncInterval) return;
            _syncTimer = 0f;
            PushTelemetry();
            if (client != null && client.IsConnected) client.SyncState(_view);
        }

        /// <summary>把车辆遥测刷进视图（速度/档位由 VehicleController 提供）。</summary>
        public void PushTelemetry()
        {
            if (vehicle == null) return;
            _view.SpeedKmh = vehicle.SpeedKmh;
            _view.Gear = vehicle.Gear;
        }

        /// <summary>上报本地物理事件（撞车/闯红灯…），成功时以服务端回推为准。</summary>
        public void ReportLocalEvent(string kind)
        {
            StateView.LocalEvent evt;
            bool known = StateView.TryGetLocalEvent(kind, out evt);
            if (!known)
            {
                Debug.LogWarning("[GameStateManager] 未知本地事件: " + kind);
                return;
            }

            if (client != null && client.IsConnected && client.SendLocalEvent(kind))
            {
                return; // 服务端 state_delta 回推后由 HandleTurnEvent 落账
            }

            // 断线兜底
            _view.ApplyLocalEventOffline(kind);
        }

        public void SetScene(string newSceneId, string newWeather, string newTimeOfDay, string newLevelId)
        {
            _view.SceneId = newSceneId;
            _view.Weather = newWeather;
            _view.TimeOfDay = newTimeOfDay;
            _view.LevelId = newLevelId;
            PushTelemetry();
            if (client != null && client.IsConnected) client.SyncState(_view);
        }

        void OnViewChanged(float trust, Emotion emotion, float delta)
        {
            if (StateChanged != null) StateChanged(trust, emotion, delta);
        }

        void OnLocalEventApplied(string kind, string description)
        {
            if (LocalEventApplied != null) LocalEventApplied(kind, description);
        }
    }
}
