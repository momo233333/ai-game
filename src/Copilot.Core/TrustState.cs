using System;
using System.Collections.Generic;

namespace Copilot.Core
{
    /// <summary>
    /// 信任值 + 4 档情绪有限状态机。Unity 与 AI 伴生进程引用同一份规则，
    /// 避免表现层与对话层算出的情绪不一致（IT方案1.md V2.0 §6.2 / §5.3）。
    /// </summary>
    public sealed class TrustState
    {
        public const float InitialTrust = 60f;
        public const float MinTrust = 0f;
        public const float MaxTrust = 100f;

        /// <summary>Unity 侧本地事件对信任值的影响（物理层最可信，见方案 §6.2）。</summary>
        public static readonly IReadOnlyDictionary<string, float> LocalEvents =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                { "collision", -12f },
                { "red_light", -8f },
                { "lane_departure", -6f },
                { "hard_brake", -4f },
                { "overspeed", -3f },
                { "smooth_drive", 1f },
                { "clean_pass", 2f },
            };

        public float Trust { get; private set; }
        public Emotion Emotion { get; private set; }

        public TrustState(float trust = InitialTrust, Emotion emotion = Emotion.Calm)
        {
            Trust = Clamp(trust);
            Emotion = emotion;
        }

        public static float Clamp(float trust)
        {
            if (trust < MinTrust) return MinTrust;
            if (trust > MaxTrust) return MaxTrust;
            return trust;
        }

        /// <summary>应用一次信任变化，返回实际增量并刷新情绪档位。</summary>
        public float Apply(float delta)
        {
            float before = Trust;
            Trust = Clamp(Trust + delta);
            Emotion = NextEmotion(Trust, Emotion, delta);
            return Trust - before;
        }

        /// <summary>应用一个本地事件（撞车/闯红灯…），未知事件返回 0 且不改变状态。</summary>
        public float ApplyLocalEvent(string eventName)
        {
            if (eventName == null || !LocalEvents.TryGetValue(eventName, out float delta)) return 0f;
            return Apply(delta);
        }

        /// <summary>用 Unity 同步过来的权威状态覆盖本地值（SyncGameState）。</summary>
        public void Set(float trust, Emotion emotion)
        {
            Trust = Clamp(trust);
            Emotion = emotion;
        }

        /// <summary>情绪档位规则：先看本回合是否发生严重负面事件，再按信任值分档。</summary>
        public static Emotion NextEmotion(float trust, Emotion current, float delta)
        {
            if (delta <= -8f) return Emotion.Nervous;
            if (trust >= 70f) return Emotion.Happy;
            if (trust >= 45f) return Emotion.Calm;
            if (trust > 25f) return Emotion.Nervous;
            return Emotion.Annoyed;
        }

        public TrustState Clone() => new TrustState(Trust, Emotion);
    }
}