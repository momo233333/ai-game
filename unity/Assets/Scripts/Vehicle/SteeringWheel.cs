using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 方向盘随转向联动（《驾考宝典》第一人称必备的表现细节）。
    /// 读 <see cref="VehicleController.CurrentSteer"/>（-0.8..0.8）映射到 ±<see cref="maxAngle"/>，
    /// 用指数平滑追上去，避免转向数值抖动时方向盘跟着抽。
    /// </summary>
    public sealed class SteeringWheel : MonoBehaviour
    {
        public VehicleController vehicle;

        [Tooltip("满舵时方向盘转多少度（真车约 540，游戏里 180 视觉更清楚）")]
        public float maxAngle = 180f;

        [Tooltip("跟随速度，越大越跟手")]
        public float followSpeed = 8f;

        [Tooltip("左右打方向是否反号。若发现打左舵方向盘却往右转，把这个勾上")]
        public bool invert = false;

        /// <summary>当前方向盘角度（度，绕 Z 轴）。学员模型的手臂按它跟转。</summary>
        public float Angle { get; private set; }

        void Update()
        {
            float steer = vehicle != null ? vehicle.CurrentSteer : 0f;
            float target = (invert ? steer : -steer) * maxAngle;
            Angle = Mathf.Lerp(Angle, target, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
            transform.localRotation = Quaternion.Euler(0f, 0f, Angle);
        }
    }
}