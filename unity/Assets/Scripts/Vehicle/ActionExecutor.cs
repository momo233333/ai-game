using System;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// AI 指令 → 物理层的中间层（方案 §5.1）：
    /// 收 drive_action 先限幅 + 白名单校验，再在 FixedUpdate 按 hold_ms 平滑插值下发给车辆。
    /// 车辆永远只认识这里输出的归一化量，LLM 抖不到 WheelCollider。
    /// </summary>
    public sealed class ActionExecutor : MonoBehaviour
    {
        public AIClient client;
        public VehicleController vehicle;

        [Tooltip("客户端兜底限幅（与服务端 ActionParser 双保险）")]
        public float maxSteer = ActionExecutorCore.MaxSteer;

        readonly ActionExecutorCore _core = new ActionExecutorCore();

        public ActionExecutorCore Core
        {
            get { return _core; }
        }

        /// <summary>最近一次指令（UI 可用于显示意图动效）。</summary>
        public DriveAction Current
        {
            get { return _core.Current; }
        }

        public event Action<DriveAction> ActionReceived;

        AIClient _boundClient;

        void OnEnable()
        {
            Rebind();
        }

        /// <summary>
        /// 绑定客户端。AddComponent 会在字段注入前就触发 OnEnable，
        /// 所以装配方（CopilotBootstrap）注入后还要再调一次 —— 本方法幂等。
        /// </summary>
        public void Rebind()
        {
            if (_boundClient == client) return;
            if (_boundClient != null) _boundClient.TurnEventReceived -= HandleTurnEvent;
            _boundClient = client;
            if (_boundClient != null) _boundClient.TurnEventReceived += HandleTurnEvent;
        }

        void OnDisable()
        {
            if (_boundClient != null)
            {
                _boundClient.TurnEventReceived -= HandleTurnEvent;
                _boundClient = null;
            }
        }

        void Start()
        {
            Rebind();
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null || evt.Kind != "drive_action") return;
            Execute(evt.ToDriveAction());
        }

        /// <summary>直接执行一条动作（本地调试 / 单测路径也可用）。</summary>
        public DriveAction Execute(DriveAction action)
        {
            var applied = _core.Execute(action);
            if (ActionReceived != null) ActionReceived(applied);
            return applied;
        }

        void FixedUpdate()
        {
            float steer, throttle, brake;
            _core.Tick(Time.fixedDeltaTime * 1000f, out steer, out throttle, out brake);
            if (vehicle != null) vehicle.ApplyInput(steer, throttle, brake);
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying) return;
            var action = _core.Current;
            Gizmos.color = Color.cyan;
            Vector3 origin = transform.position + Vector3.up * 1.5f;
            Vector3 direction = Quaternion.Euler(0f, action.Steering * 45f, 0f) * transform.forward;
            Gizmos.DrawRay(origin, direction * 4f);
        }
    }
}
