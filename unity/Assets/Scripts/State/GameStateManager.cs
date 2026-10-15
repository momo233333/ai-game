using System;
using System.Collections.Generic;
using Copilot.AI;
using Copilot.Core;
using Copilot.Vehicle;
using UnityEngine;

namespace Copilot.State
{
    /// <summary>
    /// 信任 / 情绪 / 遥测的唯一表现层数据源（方案 §5.2）。
    ///
    /// 规则全部来自共享库 <see cref="TrustState"/>（服务端同一份），Unity 侧不另写一套，
    /// 因此表现层的情绪档位与对话层算出来的必然一致。
    ///
    /// 权威顺序：
    /// ① 服务端 <c>state_delta</c> → 直接覆盖本地（含回合内的信任变化）；
    /// ② 本地物理事件（撞车/闯红灯…）→ 本地按 <see cref="TrustState.ApplyLocalEvent"/> 落账，
    ///    再 <c>SyncGameState</c> 把结果告诉服务端，下一回合它就按这个信仼值说话；
    /// ③ 每 <see cref="syncInterval"/> 秒回推一次遥测（速度/档位）。
    /// </summary>
    public sealed class GameStateManager : MonoBehaviour
    {
        public CopilotConnection connection;
        public VehicleController vehicle;

        [Header("同步")]
        [Tooltip("向服务端回推遥测的周期（秒）")]
        public float syncInterval = 1.0f;

        [Header("场景标识（随 SyncGameState 上报）")]
        public string levelId = "level_01";
        public string sceneId = "city_01";
        public string weather = "晴";
        public string timeOfDay = "傍晚";

        /// <summary>本地事件的说明文案（用于 HUD 飘条）。</summary>
        static readonly Dictionary<string, string> EventDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "collision", "撞车了" },
            { "red_light", "闯红灯" },
            { "lane_departure", "压线了" },
            { "hard_brake", "急刹" },
            { "overspeed", "超速" },
            { "smooth_drive", "开得稳" },
            { "clean_pass", "干净通过" },
        };

        readonly TrustState _trust = new TrustState();
        float _syncTimer;

        public float Trust
        {
            get { return _trust.Trust; }
        }

        public Emotion Emotion
        {
            get { return _trust.Emotion; }
        }

        public event Action<float, Emotion, float> StateChanged;
        public event Action<string, string> LocalEventApplied;

        CopilotConnection _bound;

        void OnEnable()
        {
            Rebind();
        }

        /// <summary>幂等绑定（AddComponent 会在字段注入前触发 OnEnable，装配方注入后再调一次）。</summary>
        public void Rebind()
        {
            if (_bound == connection) return;
            if (_bound != null)
            {
                _bound.TurnEventReceived -= HandleTurnEvent;
                _bound.Connected -= HandleConnected;
            }
            _bound = connection;
            if (_bound != null)
            {
                _bound.TurnEventReceived += HandleTurnEvent;
                _bound.Connected += HandleConnected;
            }
        }

        void OnDisable()
        {
            if (_bound == null) return;
            _bound.TurnEventReceived -= HandleTurnEvent;
            _bound.Connected -= HandleConnected;
            _bound = null;
        }

        void Start()
        {
            Rebind();
        }

        void HandleConnected()
        {
            PushState();
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null || evt.Kind != TurnEventKind.StateDelta || evt.State == null) return;

            _trust.Set(evt.State.Trust, evt.State.Emotion);
            if (StateChanged != null) StateChanged(_trust.Trust, _trust.Emotion, evt.State.TrustDelta);
        }

        void Update()
        {
            _syncTimer += Time.deltaTime;
            if (_syncTimer < syncInterval) return;
            _syncTimer = 0f;
            PushState();
        }

        /// <summary>上报本地物理事件（撞车/闯红灯…）。</summary>
        public void ReportLocalEvent(string kind)
        {
            float delta = _trust.ApplyLocalEvent(kind);
            if (delta == 0f && !EventDescriptions.ContainsKey(kind ?? ""))
            {
                Debug.LogWarning("[GameStateManager] 未知本地事件: " + kind);
                return;
            }

            string description;
            if (!EventDescriptions.TryGetValue(kind, out description)) description = kind;

            if (StateChanged != null) StateChanged(_trust.Trust, _trust.Emotion, delta);
            if (LocalEventApplied != null) LocalEventApplied(kind, description);

            PushState();
        }

        /// <summary>重开一局：信任回到初始值，并把结果同步给服务端（SyncGameState 会覆盖它那边的值）。</summary>
        public void ResetTrust()
        {
            _trust.Set(TrustState.InitialTrust, Emotion.Calm);
            if (StateChanged != null) StateChanged(_trust.Trust, _trust.Emotion, 0f);
            PushState();
        }

        public void SetScene(string newSceneId, string newWeather, string newTimeOfDay, string newLevelId)
        {
            sceneId = newSceneId;
            weather = newWeather;
            timeOfDay = newTimeOfDay;
            levelId = newLevelId;
            PushState();
        }

        /// <summary>把当前信任/情绪/遥测拼成快照回推给 AI 服务。</summary>
        public void PushState()
        {
            if (connection == null || !connection.IsConnected) return;

            var snapshot = new GameStateSnapshot
            {
                Trust = _trust.Trust,
                Emotion = _trust.Emotion,
                LevelId = levelId,
                Scene = new SceneContext { SceneId = sceneId, Weather = weather, TimeOfDay = timeOfDay },
                Telemetry = new VehicleTelemetry
                {
                    SpeedKmh = vehicle != null ? vehicle.SpeedKmh : 0f,
                    Gear = vehicle != null ? vehicle.Gear : 0,
                },
            };

            connection.SyncState(snapshot);
        }
    }
}