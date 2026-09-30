using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 物理参数外置（方案 §5.1）：调手感不动代码。
    /// </summary>
    [CreateAssetMenu(menuName = "副驾指令/车辆物理档案", fileName = "VehiclePhysicsProfile")]
    public sealed class VehiclePhysicsProfile : ScriptableObject
    {
        [Header("转向")]
        [Range(5f, 50f)] public float maxSteerAngle = 32f;
        [Range(0f, 1f)] public float steerLerpSpeed = 8f;

        [Header("动力")]
        [Range(100f, 3000f)] public float motorTorque = 1400f;
        [Range(100f, 8000f)] public float brakeTorque = 3500f;
        [Range(30f, 250f)] public float maxSpeedKmh = 140f;

        [Header("手感")]
        [Tooltip("AI 指令 throttle 到 motorTorque 的曲线（0..1 归一化）")]
        public AnimationCurve throttleCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("AI 指令 brake 到 brakeTorque 的曲线")]
        public AnimationCurve brakeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    }
}
