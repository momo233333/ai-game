using System;
using Copilot.Gameplay;
using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 车辆**测量**层（挂 <c>Car</c>）：横向偏移 / 前方障碍距离 / 撞车。
    ///
    /// 只测量，不判定 —— 阈值与结果处理在 <c>Copilot.Core.Gameplay</c>（纯逻辑可单测）。
    /// 也**不碰 <c>GameStateManager</c>**：上报信任的副作用只有一个出口（<c>MistakeRunner</c>），
    /// 否则一次撞车会被记两次。
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class VehicleSensors : MonoBehaviour
    {
        public VehicleController vehicle;
        public RoadGeometry road;

        [Header("阈值")]
        [Tooltip("低于这个速度不判车道几何有效性（停着不动时偏移没有意义）")]
        public float speedThresholdKmh = 5f;

        [Tooltip("低于这个撞击速度不算撞车（蹭路缘、轮胎接地都低于它）")]
        public float collisionImpactKmh = 22f;

        [Tooltip("非失误类的「压线」独立上报阈值")]
        public float laneDepartureToleranceM = 1.0f;

        [Tooltip("压线要连续这么久才上报（防单帧抖动）")]
        public float laneDepartureDwellMs = 400f;

        [Tooltip("前方障碍离车头轴线多远还算「同车道」")]
        public float hazardLateralGateM = 4f;

        [Header("前方障碍")]
        [Tooltip("由 MistakeRunner 在失误开演时摆到车头正前方；平时是隐藏的")]
        public Transform hazardCar;

        public float SpeedKmh { get; private set; }

        /// <summary>离本车道中心的有符号横向偏移（右为正）；几何无效时恒为 0。</summary>
        public float LateralOffsetM { get; private set; }

        /// <summary>
        /// 横向偏移判据此刻是否成立。**转弯中 / 路口内 / 速度过低都为 false**
        /// —— 那些情况下"离 Z 向车道中心的偏移"根本不成立。
        /// </summary>
        public bool LaneGeometryValid { get; private set; }

        public bool InsideIntersection { get; private set; }

        /// <summary>沿车头方向到前方障碍的距离；没有障碍给 <c>PositiveInfinity</c>。</summary>
        public float DistanceAheadToHazardM { get; private set; } = float.PositiveInfinity;

        /// <summary>撞车（已过滤掉路面/路缘/低速接触）。参数：撞击速度 km/h、对方名字。</summary>
        public event Action<float, string> CollisionDetected;

        /// <summary>非失误类的独立压线（带迟滞）。失误窗口开着时由 MistakeRunner 吞掉。</summary>
        public event Action LaneDeparted;

        float _laneDwellMs;

        /// <summary>路面与地面物件：撞到它们不算撞车（轮胎接地也会触发回调）。</summary>
        static readonly string[] GroundNames =
        {
            "Road", "CrossRoad", "Sidewalk", "Curb", "LaneDash", "LaneEdge", "CenterLine", "Zebra",
        };

        /// <summary>
        /// 撞车是否该上报（纯谓词，便于单测）。三重过滤：
        /// ① 对方是轮子（轮地接触会触发回调）② 对方是路面/地面 ③ 撞击速度不够。
        /// </summary>
        public static bool IsReportableCollision(string otherName, bool otherIsWheel,
            float impactKmh, float minImpactKmh)
        {
            if (otherIsWheel) return false;
            if (!string.IsNullOrEmpty(otherName) && Array.IndexOf(GroundNames, otherName) >= 0) return false;
            return impactKmh >= minImpactKmh;
        }

        void Update()
        {
            var t = transform;
            SpeedKmh = vehicle != null ? vehicle.SpeedKmh : 0f;

            bool hasRoad = road != null;
            InsideIntersection = hasRoad && road.IsInsideIntersection(t.position, 4f);

            LaneGeometryValid = hasRoad
                                && road.IsHeadingAlongRoad(t)
                                && !InsideIntersection
                                && SpeedKmh >= speedThresholdKmh;

            LateralOffsetM = LaneGeometryValid
                ? t.position.x - road.NearestLaneCenterX(t.position.x)
                : 0f;

            TrackStandaloneLaneDeparture();
            DistanceAheadToHazardM = MeasureHazardAhead();
        }

        /// <summary>非失误类的压线（比如玩家让她瞎打方向），带迟滞避免抖动。</summary>
        void TrackStandaloneLaneDeparture()
        {
            if (!LaneGeometryValid || Mathf.Abs(LateralOffsetM) < laneDepartureToleranceM)
            {
                _laneDwellMs = 0f;
                return;
            }

            _laneDwellMs += Time.deltaTime * 1000f;
            if (_laneDwellMs < laneDepartureDwellMs) return;

            _laneDwellMs = 0f;
            if (LaneDeparted != null) LaneDeparted();
        }

        float MeasureHazardAhead()
        {
            if (hazardCar == null || !hazardCar.gameObject.activeInHierarchy) return float.PositiveInfinity;

            Vector3 delta = hazardCar.position - transform.position;
            delta.y = 0f;

            float ahead = Vector3.Dot(delta, transform.forward);
            if (ahead <= 0f) return float.PositiveInfinity;                        // 不在前方

            float lateral = Mathf.Abs(Vector3.Dot(delta, transform.right));
            if (lateral > hazardLateralGateM) return float.PositiveInfinity;       // 不在同一车道附近

            return ahead;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (collision == null) return;

            var other = collision.collider;
            bool otherIsWheel = other != null && other.GetComponent<WheelCollider>() != null;
            string otherName = other != null ? other.gameObject.name : string.Empty;
            float impactKmh = collision.relativeVelocity.magnitude * 3.6f;

            if (!IsReportableCollision(otherName, otherIsWheel, impactKmh, collisionImpactKmh)) return;

            // 记下对方与撞击速度：将来若出现误报，日志里能直接认出是撞了什么
            Debug.Log(string.Format("[Sensors] 撞车：{0} · 撞击 {1:0} km/h", otherName, impactKmh));
            if (CollisionDetected != null) CollisionDetected(impactKmh, otherName);
        }
    }
}
