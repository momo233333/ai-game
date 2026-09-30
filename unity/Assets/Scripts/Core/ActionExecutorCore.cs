using System;

namespace Copilot.Core
{
    /// <summary>
    /// 指令平滑执行核心（方案 §5.1）：AI 指令不直接写 WheelCollider，
    /// 先限幅，再按 hold_ms 做 SmoothStep 插值，避免离散指令导致车辆抖动。
    /// 纯数学，无 UnityEngine 依赖；由 ActionExecutor(MonoBehaviour) 在 FixedUpdate 里驱动。
    /// </summary>
    public sealed class ActionExecutorCore
    {
        public const float MaxSteer = 0.8f;
        public const int MinHoldMs = 0;
        public const int MaxHoldMs = 5000;

        DriveAction _current = new DriveAction();
        float _elapsedMs;
        bool _unknownIntentSeen;

        public DriveAction Current
        {
            get { return _current; }
        }

        public float ElapsedMs
        {
            get { return _elapsedMs; }
        }

        /// <summary>最近一次被客户端拦截的非法 intent（防御纵深，服务端已拦一道）。</summary>
        public bool UnknownIntentSeen
        {
            get { return _unknownIntentSeen; }
        }

        public static DriveAction Clamp(DriveAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var clamped = new DriveAction
            {
                Steering = Clampf(action.Steering, -MaxSteer, MaxSteer),
                Throttle = Clampf(action.Throttle, 0f, 1f),
                Brake = Clampf(action.Brake, 0f, 1f),
                HoldMs = Clampi(action.HoldMs, MinHoldMs, MaxHoldMs),
                Intent = action.Intent,
                Confidence = Clampf(action.Confidence, 0f, 1f),
                Refused = action.Refused,
                RefuseReason = action.RefuseReason,
            };
            return clamped;
        }

        /// <summary>下发新指令，重置计时。白名单外的 intent 按 IGNORE 处理。</summary>
        public DriveAction Execute(DriveAction action)
        {
            var safe = Clamp(action ?? new DriveAction());
            if (!Intents.IsAllowed(safe.Intent))
            {
                _unknownIntentSeen = true;
                safe.Intent = Intents.Ignore;
                safe.Throttle = 0f;
                safe.Brake = 0.6f;
            }
            _current = safe;
            _elapsedMs = 0f;
            return _current;
        }

        /// <summary>
        /// 推进 dtMs 毫秒，返回本帧应施加的转向/油门/刹车。
        /// hold 到期后回到中位（转向 0、无油门、轻刹保持停车意图由车辆自己决定）。
        /// </summary>
        public void Tick(float dtMs, out float steering, out float throttle, out float brake)
        {
            if (dtMs < 0f) dtMs = 0f;
            _elapsedMs += dtMs;

            int holdMs = _current.HoldMs;
            if (holdMs <= 0)
            {
                steering = 0f;
                throttle = 0f;
                brake = 0f;
                return;
            }

            float t = Clampf(_elapsedMs / holdMs, 0f, 1f);
            if (t >= 1f)
            {
                steering = 0f;
                throttle = 0f;
                brake = 0f;
                return;
            }

            float eased = SmoothStep(t);
            steering = _current.Steering * eased;
            throttle = _current.Throttle * eased;
            brake = _current.Brake * eased;
        }

        public void Reset()
        {
            _current = new DriveAction();
            _elapsedMs = 0f;
            _unknownIntentSeen = false;
        }

        static float SmoothStep(float t)
        {
            t = Clampf(t, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        static float Clampf(float value, float low, float high)
        {
            return value < low ? low : (value > high ? high : value);
        }

        static int Clampi(int value, int low, int high)
        {
            return value < low ? low : (value > high ? high : value);
        }
    }
}
