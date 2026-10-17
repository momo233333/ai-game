using System;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// AI 指令 → 物理层的中间层（方案 §5.1）：
    /// 收 <c>drive_action</c> 先过共享的 <see cref="ActionLimits"/>（白名单 + 场景规则 + 限幅），
    /// 再在 FixedUpdate 按 <c>hold_ms</c> 平滑下发。车辆永远只认识归一化量。
    ///
    /// 限幅/白名单用的是 <c>src/Copilot.Core/ActionLimits.cs</c> **与服务端同一份代码**，
    /// 不在 Unity 侧另写一遍 —— 两端规则不可能漂移。
    /// </summary>
    public sealed class ActionExecutor : MonoBehaviour
    {
        public CopilotConnection connection;
        public VehicleController vehicle;

        [Tooltip("下发插值速率：满量程所需毫秒数")]
        public float rampMs = 150f;

        [Tooltip("定速：玩家原话里带速度（「5km/h」「20 公里」）时由代码闭环保持；关掉就只认油门/刹车指令")]
        public bool cruiseEnabled = true;

        DriveAction _target;
        float _elapsedMs;
        bool _active;
        float _steer;
        float _throttle;
        float _brake;

        /// <summary>最近一次指令（UI 可用于显示意图动效）。</summary>
        public DriveAction Current { get; private set; }

        public event Action<DriveAction> ActionReceived;

        CopilotConnection _bound;

        void OnEnable()
        {
            Rebind();
        }

        /// <summary>幂等绑定（AddComponent 会在字段注入前触发 OnEnable，装配方注入后再调一次）。</summary>
        public void Rebind()
        {
            if (_bound == connection) return;
            if (_bound != null) _bound.TurnEventReceived -= HandleTurnEvent;
            _bound = connection;
            if (_bound != null) _bound.TurnEventReceived += HandleTurnEvent;
        }

        void OnDisable()
        {
            if (_bound != null)
            {
                _bound.TurnEventReceived -= HandleTurnEvent;
                _bound = null;
            }
        }

        void Start()
        {
            Rebind();
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null) return;

            // 定速归代码：意图集里没有任何一个能承接「5km/h」这个数，所以从**原话**里抽
            // （与「方向判定归代码」同一思路）。识别文本在 AsrFinal 上，值交给 VehicleController 闭环保持。
            if (evt.Kind == TurnEventKind.AsrFinal)
            {
                if (!cruiseEnabled || vehicle == null) return;

                int target = Intents.ParseTargetSpeedKmh(evt.Text);
                if (target > 0)
                {
                    vehicle.SetTargetSpeed(target);
                    Debug.Log($"[Cruise] 听见「{evt.Text}」→ 定速 {target} km/h");
                }
                else if (Intents.IsSlowRequest(evt.Text) && vehicle.SpeedKmh > 0.5f)
                {
                    // 「慢一点」这类没给数字的降速要求：按**当前车速打六折**（下限 3km/h）。
                    // 车已经停了就不动它 —— 否则一句「慢一点」会把停着的车推出去。
                    float slow = Mathf.Max(3f, vehicle.SpeedKmh * 0.6f);
                    vehicle.SetTargetSpeed(slow);
                    Debug.Log($"[Cruise] 听见「{evt.Text}」→ 降到 {slow:0.#} km/h（当时 {vehicle.SpeedKmh:0.#}）");
                }
                return;
            }

            if (evt.Kind != TurnEventKind.DriveAction || evt.Action == null) return;

            // 刹车 / 停车 / 拒绝（危险指令）→ 退出定速：玩家在要减速，别跟她抢
            if (cruiseEnabled && vehicle != null
                && (evt.Action.Intent == Intents.Brake
                    || evt.Action.Intent == Intents.Park
                    || evt.Action.Intent == Intents.Ignore))
            {
                vehicle.ClearTargetSpeed();
            }

            Execute(evt.Action);
        }

        /// <summary>执行一条动作（本地调试 / 单测路径也可用）。返回实际生效的（已限幅）动作。</summary>
        public DriveAction Execute(DriveAction action)
        {
            var applied = ActionLimits.Clamp(action);
            Current = applied;
            _target = applied;
            _elapsedMs = 0f;
            // IGNORE / QUESTION_BACK 只给一个极短的「无动力」占位，别让车莫名前冲
            _active = applied.Intent != Intents.Ignore && applied.Intent != Intents.QuestionBack;
            if (ActionReceived != null) ActionReceived(applied);
            return applied;
        }

        /// <summary>重开一局：清掉残留动作 —— 否则上一局的指令会继续把车推出去。</summary>
        public void Reset()
        {
            _target = null;
            _active = false;
            _elapsedMs = 0f;
            _steer = 0f;
            _throttle = 0f;
            _brake = 0f;
            Current = null;
            if (vehicle != null) vehicle.ClearTargetSpeed();
        }

        void FixedUpdate()
        {
            float dtMs = Time.fixedDeltaTime * 1000f;

            if (_active)
            {
                _elapsedMs += dtMs;
                if (_elapsedMs >= _target.HoldMs) _active = false;
            }

            float step = rampMs <= 0f ? 1f : dtMs / rampMs;
            _steer = Mathf.MoveTowards(_steer, _active ? _target.Steering : 0f, step);
            _throttle = Mathf.MoveTowards(_throttle, _active ? _target.Throttle : 0f, step);
            _brake = Mathf.MoveTowards(_brake, _active ? _target.Brake : 0f, step);

            if (vehicle != null) vehicle.ApplyInput(_steer, _throttle, _brake);
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || Current == null) return;
            Gizmos.color = Color.cyan;
            Vector3 origin = transform.position + Vector3.up * 1.5f;
            Vector3 direction = Quaternion.Euler(0f, Current.Steering * 45f, 0f) * transform.forward;
            Gizmos.DrawRay(origin, direction * 4f);
        }
    }
}