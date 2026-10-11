using Copilot.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.UI
{
    /// <summary>
    /// 驾驶仪表（《驾考宝典》副驾驶视角必备）：**车速**与**方向盘转角**。
    ///
    /// 为什么必须有：参考同类游戏（《教妹妹开车》Steam 3948460）的核心张力是
    /// 「**要在妹妹犯错之前及时干预**」—— 可你连她开多快、打了多少方向都看不见，
    /// 就无从判断她是不是要出事。CLI 里一直有车速输出，HUD 上却漏了这两个数。
    ///
    /// 纯表现层：只读 <see cref="VehicleController.SpeedKmh"/> 与
    /// <see cref="SteeringWheel.Angle"/>，不参与物理。
    /// </summary>
    public sealed class DriveHud : MonoBehaviour
    {
        [Header("数据源")]
        public VehicleController vehicle;
        public SteeringWheel steeringWheel;

        [Header("车速")]
        public Text speedLabel;
        [Tooltip("车速条（按 maxSpeedKmh 归一化），可不连")]
        public Image speedFill;
        [Tooltip("车速条拉满对应的速度")]
        public float maxSpeedKmh = 160f;

        [Header("方向盘转角")]
        [Tooltip("指针/滑块：在轨道上来回移动")]
        public RectTransform steerKnob;
        [Tooltip("轨道（用它的一半宽度作为满舵行程）")]
        public RectTransform steerTrack;
        public Text steerLabel;
        [Tooltip("满舵角度，与 SteeringWheel.maxAngle 保持一致")]
        public float maxWheelAngle = 180f;

        /// <summary>最近一帧的车速（供其它 UI 复用）。</summary>
        public float SpeedKmh { get; private set; }

        /// <summary>最近一帧的方向盘角度（正 = 右）。</summary>
        public float WheelAngleDeg { get; private set; }

        void LateUpdate()
        {
            UpdateSpeed();
            UpdateSteering();
        }

        void UpdateSpeed()
        {
            SpeedKmh = vehicle != null ? vehicle.SpeedKmh : 0f;

            if (speedLabel != null) speedLabel.text = Mathf.RoundToInt(SpeedKmh).ToString();
            if (speedFill != null) speedFill.fillAmount = Mathf.Clamp01(SpeedKmh / Mathf.Max(1f, maxSpeedKmh));
        }

        void UpdateSteering()
        {
            WheelAngleDeg = steeringWheel != null ? steeringWheel.Angle : 0f;

            float span = Mathf.Max(1f, maxWheelAngle);
            float normalized = Mathf.Clamp(WheelAngleDeg / span, -1f, 1f);

            if (steerLabel != null)
            {
                steerLabel.text = Mathf.Abs(WheelAngleDeg) < 2f
                    ? "方向回正"
                    : $"{(WheelAngleDeg > 0f ? "右" : "左")} {Mathf.Abs(normalized):0.00}（{Mathf.Abs(WheelAngleDeg):0}°）";
            }

            if (steerKnob != null && steerTrack != null)
            {
                float halfWidth = steerTrack.rect.width * 0.5f;
                var p = steerKnob.anchoredPosition;
                p.x = normalized * halfWidth;
                steerKnob.anchoredPosition = p;
            }
        }
    }
}