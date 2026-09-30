using System;
using System.Collections.Generic;

namespace Copilot.Core
{
    /// <summary>
    /// 信任/情绪表现层（方案 §5.2）：服务端 StateDelta 是权威值，
    /// 本地物理事件（撞车/闯红灯…）先上报服务端拿权威 delta；断线时用本地表兜底，
    /// 表项与 ai-service/core/state.py 的 LOCAL_EVENTS 一一对应。
    /// </summary>
    public sealed class StateView
    {
        public sealed class LocalEvent
        {
            public string Kind;
            public float TrustDelta;
            public string Description;

            public LocalEvent(string kind, float trustDelta, string description)
            {
                Kind = kind;
                TrustDelta = trustDelta;
                Description = description;
            }
        }

        static readonly Dictionary<string, LocalEvent> LocalEventTable = new Dictionary<string, LocalEvent>
        {
            { "collision", new LocalEvent("collision", -12f, "撞车了") },
            { "red_light", new LocalEvent("red_light", -8f, "闯红灯") },
            { "lane_cut", new LocalEvent("lane_cut", -4f, "压实线") },
            { "safe_arrival", new LocalEvent("safe_arrival", 10f, "安全到达") },
            { "smooth_drive", new LocalEvent("smooth_drive", 3f, "开得稳") },
            { "honk", new LocalEvent("honk", -2f, "乱按喇叭") },
        };

        public float Trust = 60f;
        public Emotion Emotion = Emotion.Calm;
        public string LevelId = "level_01";
        public string SceneId = "city_01";
        public string Weather = "晴";
        public string TimeOfDay = "傍晚";
        public float SpeedKmh;
        public int Gear = 1;
        public bool AtIntersection;
        public string ObstacleSummary = "";

        /// <summary>最后一次信任变化量（驱动情绪 FSM，与服务端 next_emotion 同规则）。</summary>
        public float LastDelta;

        public event Action<float, Emotion, float> StateChanged;
        public event Action<string, string> LocalEventApplied;

        public static IEnumerable<KeyValuePair<string, LocalEvent>> LocalEvents
        {
            get { return LocalEventTable; }
        }

        public static bool TryGetLocalEvent(string kind, out LocalEvent evt)
        {
            return LocalEventTable.TryGetValue(kind ?? "", out evt);
        }

        /// <summary>应用服务端回推的权威 delta。</summary>
        public void ApplyServerDelta(float trust, Emotion emotion, float trustDelta)
        {
            Trust = Clampf(trust, 0f, 100f);
            Emotion = emotion;
            LastDelta = trustDelta;
            RaiseChanged(trustDelta);
        }

        /// <summary>断线兜底：按本地表改信任，情绪用与服务端相同的规则重算。</summary>
        public bool ApplyLocalEventOffline(string kind)
        {
            LocalEvent evt;
            if (!TryGetLocalEvent(kind, out evt)) return false;
            ApplyOfflineDelta(evt.TrustDelta);
            if (LocalEventApplied != null) LocalEventApplied(evt.Kind, evt.Description);
            return true;
        }

        public void ApplyOfflineDelta(float delta)
        {
            float before = Trust;
            Trust = Clampf(before + delta, 0f, 100f);
            float applied = Trust - before;
            LastDelta = applied;
            Emotion = NextEmotion(Trust, applied, AtIntersection, ObstacleSummary);
            RaiseChanged(applied);
        }

        /// <summary>与 ai-service/core/state.py next_emotion() 保持一致。</summary>
        public static Emotion NextEmotion(float trust, float lastDelta, bool atIntersection, string obstacleSummary)
        {
            if (trust < 20f) return Emotion.Nervous;
            if (lastDelta <= -5f || trust < 40f) return Emotion.Annoyed;
            if (trust >= 80f && lastDelta >= 0f) return Emotion.Happy;
            if (atIntersection && (obstacleSummary ?? "").Contains("红灯")) return Emotion.Nervous;
            return Emotion.Calm;
        }

        /// <summary>把本地状态打包成 sync_state 请求体（JSON-TCP 协议）。</summary>
        public Dictionary<string, object> ToSyncPayload()
        {
            return new Dictionary<string, object>
            {
                { "trust", (double)Trust },
                { "emotion", EmotionUtil.ToName(Emotion) },
                { "level_id", LevelId },
                { "scene", new Dictionary<string, object>
                    {
                        { "scene_id", SceneId },
                        { "weather", Weather },
                        { "time_of_day", TimeOfDay },
                    }
                },
                { "telemetry", new Dictionary<string, object>
                    {
                        { "speed_kmh", (double)SpeedKmh },
                        { "gear", Gear },
                        { "at_intersection", AtIntersection },
                        { "obstacle_summary", ObstacleSummary ?? "" },
                    }
                },
            };
        }

        /// <summary>用 state_query / sync_state 返回的完整状态刷新本地视图。</summary>
        public void LoadSnapshot(Dictionary<string, object> data)
        {
            if (data == null) return;
            Trust = Clampf(SimpleJson.GetFloat(data, "trust", Trust), 0f, 100f);
            Emotion = EmotionUtil.FromName(SimpleJson.GetString(data, "emotion", EmotionUtil.ToName(Emotion)));
            LevelId = SimpleJson.GetString(data, "level_id", LevelId);

            var scene = SimpleJson.AsDict(data.ContainsKey("scene") ? data["scene"] : null);
            if (scene != null)
            {
                SceneId = SimpleJson.GetString(scene, "scene_id", SceneId);
                Weather = SimpleJson.GetString(scene, "weather", Weather);
                TimeOfDay = SimpleJson.GetString(scene, "time_of_day", TimeOfDay);
            }

            var telemetry = SimpleJson.AsDict(data.ContainsKey("telemetry") ? data["telemetry"] : null);
            if (telemetry != null)
            {
                SpeedKmh = SimpleJson.GetFloat(telemetry, "speed_kmh", SpeedKmh);
                Gear = SimpleJson.GetInt(telemetry, "gear", Gear);
                AtIntersection = SimpleJson.GetBool(telemetry, "at_intersection", AtIntersection);
                ObstacleSummary = SimpleJson.GetString(telemetry, "obstacle_summary", ObstacleSummary);
            }
            RaiseChanged(LastDelta);
        }

        void RaiseChanged(float delta)
        {
            if (StateChanged != null) StateChanged(Trust, Emotion, delta);
        }

        static float Clampf(float value, float low, float high)
        {
            return value < low ? low : (value > high ? high : value);
        }
    }
}
