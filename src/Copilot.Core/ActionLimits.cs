using System;

namespace Copilot.Core
{
    /// <summary>安全守卫结果。</summary>
    public sealed class GuardResult
    {
        /// <summary>经限幅/拦截后的可执行指令。</summary>
        public DriveAction Action { get; set; } = new DriveAction();

        /// <summary>被场景规则拒绝时，要求对话层改写的台词；否则为 null。</summary>
        public string? ForcedReply { get; set; }

        /// <summary>是否发生了拦截（白名单外或场景规则拒绝）。</summary>
        public bool Intercepted { get; set; }
    }

    /// <summary>
    /// 指令解析与限幅层（IT方案1.md V2.0 §5.4）。
    /// GBNF grammar 只保证 JSON 结构合法，不保证数值在物理可行域内，
    /// 因此所有 LLM 指令都必须过这里。
    /// </summary>
    public static class ActionLimits
    {
        public const float MaxSteer = 0.8f;
        public const float MinHoldMs = 200f;
        public const float MaxHoldMs = 5000f;
        public const float MaxHoldSpeedKmh = 30f;

        /// <summary>值域限幅：转向 ±0.8、油门 [-1,1]（**负 = 倒车**）、刹车 [0,1]、hold_ms [200,5000]。</summary>
        public static DriveAction Clamp(DriveAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var a = action.Clone();
            a.Steering = ClampF(a.Steering, -MaxSteer, MaxSteer);
            // 负油门是**倒车**，不是错误值，别夹掉
            a.Throttle = ClampF(a.Throttle, -1f, 1f);
            a.Brake = ClampF(a.Brake, 0f, 1f);
            a.HoldMs = (int)ClampF(a.HoldMs, MinHoldMs, MaxHoldMs);
            a.Confidence = ClampF(a.Confidence, 0f, 1f);
            return a;
        }

        /// <summary>白名单外 intent 一律降级为 IGNORE，绝不让未知指令落到物理层。</summary>
        public static bool IsAllowedIntent(string intent) => Intents.IsAllowed(intent);

        /// <summary>场景规则：高速行驶中不允许 PARK / 大幅转向。</summary>
        public static bool ViolatesSceneRules(DriveAction action, GameStateSnapshot state)
        {
            if (action == null || state == null) return false;
            float speed = state.Telemetry?.SpeedKmh ?? 0f;

            if (string.Equals(action.Intent, Intents.Park, StringComparison.Ordinal)
                && speed > MaxHoldSpeedKmh)
            {
                return true;
            }

            // 高速下的急打方向（转向幅度超过 0.5）同样拒绝
            if (speed > 60f && Math.Abs(action.Steering) > 0.5f)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 意图 → 规范动作参数表。
        /// LLM 只负责决定「做什么」(intent) 与台词，**不给它发明数值的权利**：
        /// 实测 7B 模型在 Prompt 未指定数值时会一律输出 0，导致车不动。
        /// 规则后端与真实 LLM 后端共用这张表，保证同意图的车感一致。
        /// </summary>
        public static DriveAction Canonical(string intent)
        {
            switch (intent)
            {
                case Intents.TurnLeft:
                    return new DriveAction { Intent = intent, Steering = -0.6f, Throttle = 0.3f, HoldMs = 1200, Confidence = 0.9f };
                case Intents.TurnRight:
                    return new DriveAction { Intent = intent, Steering = 0.6f, Throttle = 0.3f, HoldMs = 1200, Confidence = 0.9f };
                case Intents.Accelerate:
                    return new DriveAction { Intent = intent, Throttle = 0.8f, HoldMs = 1500, Confidence = 0.9f };
                case Intents.Brake:
                    return new DriveAction { Intent = intent, Brake = 0.8f, HoldMs = 1500, Confidence = 0.9f };
                case Intents.LaneChange:
                    return new DriveAction { Intent = intent, Steering = 0.3f, Throttle = 0.4f, HoldMs = 1500, Confidence = 0.9f };
                case Intents.Park:
                    return new DriveAction { Intent = intent, Brake = 0.6f, HoldMs = 2000, Confidence = 0.9f };
                case Intents.Reverse:
                    // 倒车 = 负油门（见 Intents.Reverse 的说明）
                    return new DriveAction { Intent = intent, Throttle = -0.45f, HoldMs = 1800, Confidence = 0.9f };
                default:
                    // IGNORE / QUESTION_BACK / 未知：不给任何动力
                    return new DriveAction { Intent = intent, HoldMs = 500, Confidence = 1f };
            }
        }

        /// <summary>
        /// 把「往哪边」补进中立意图的转向里（<paramref name="direction"/>：-1 左 / +1 右 / 0 不管）。
        ///
        /// 只对**本身不带方向的横向动作**生效（目前只有 <see cref="Intents.LaneChange"/>）。
        /// TURN_LEFT/TURN_RIGHT 的方向已经写在意图里，不该被这里覆盖。
        /// 这一步必须排在 <see cref="Guard"/>（里面会套 Canonical）**之后**，否则会被 +0.3 覆盖回去。
        /// </summary>
        public static void ApplyDirection(DriveAction action, int direction)
        {
            if (action == null || direction == 0) return;
            if (!string.Equals(action.Intent, Intents.LaneChange, StringComparison.Ordinal)) return;

            float magnitude = Math.Abs(action.Steering);
            if (magnitude < 0.01f) magnitude = Math.Abs(Canonical(Intents.LaneChange).Steering);
            action.Steering = direction < 0 ? -magnitude : magnitude;
        }

        /// <summary>LLM 没给数值（全 0）时套用规范参数；给了数值就按原值走限幅。</summary>
        public static DriveAction ApplyCanonicalIfUnspecified(DriveAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            bool unspecified = action.Steering == 0f && action.Throttle == 0f
                               && action.Brake == 0f && action.HoldMs == 0;
            if (!unspecified) return action;

            var canonical = Canonical(action.Intent);
            canonical.Confidence = action.Confidence > 0f ? action.Confidence : canonical.Confidence;
            return canonical;
        }

        /// <summary>
        /// 完整守卫链：白名单 → 场景规则 → 补齐规范参数 → 限幅。
        /// 被拒绝时产出强制改写的台词（拒绝话术）并下发安全指令。
        /// </summary>
        public static GuardResult Guard(DriveAction action, GameStateSnapshot state)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            if (!IsAllowedIntent(action.Intent))
            {
                return new GuardResult
                {
                    Action = new DriveAction { Intent = Intents.Ignore, Confidence = 1f },
                    ForcedReply = "啥？这指令我听不懂，你别乱指挥。",
                    Intercepted = true,
                };
            }

            var resolved = ApplyCanonicalIfUnspecified(action);

            if (ViolatesSceneRules(resolved, state))
            {
                float speed = state?.Telemetry?.SpeedKmh ?? 0f;
                return new GuardResult
                {
                    Action = new DriveAction { Intent = Intents.Ignore, Confidence = 1f },
                    ForcedReply = $"哥你疯啦？现在 {speed:0} 码你让我靠边？",
                    Intercepted = true,
                };
            }

            return new GuardResult { Action = Clamp(resolved), Intercepted = false };
        }

        private static float ClampF(float v, float min, float max)
        {
            if (float.IsNaN(v)) return min;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}