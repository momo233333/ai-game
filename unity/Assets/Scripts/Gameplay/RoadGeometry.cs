using UnityEngine;

namespace Copilot.Gameplay
{
    /// <summary>
    /// 场地几何的**唯一真源**（挂在场地根物体上）。
    ///
    /// 车道中心/宽度/弯区范围属于**场景制作数据**（和楼群坐标一个性质），不是游戏规则，
    /// 所以留在 Unity 侧；Core 只拿"离本车道中心偏了多少"这个测量结果做判定 ——
    /// 与 <c>VehicleTelemetry</c> 的分工一致。
    ///
    /// 数值来自 <c>SceneBuilder</c>，两处必须一致：
    /// - **训练场**（科目一，默认）：三条直线练习道 → 车道中心 **−3.6 / 0 / +3.6**、宽 3.6m；
    ///   `crossCenterZ` 表示**直角弯区/交叉练习区**的中心（那里不判横向偏移）。
    /// - **主路**（备用，科目三）：车道分隔虚线 x=±3.6、边线 x=±7.2 → 车道中心 −5.4/−1.8/+1.8/+5.4；
    ///   `crossCenterZ` 表示路口中心。
    ///
    /// ⚠️ `crossCenterZ / crossHalfExtentM` 的语义是**泛化的「几何复杂区」**：进这个区间就不判横向偏移
    /// （转弯中"离 Z 向车道中心多远"根本不成立）。训练场叫"弯区"，主路叫"路口"，判据是同一套。
    /// </summary>
    public sealed class RoadGeometry : MonoBehaviour
    {
        [Tooltip("场地前进方向（两个场地都是 +Z）")]
        public Vector3 roadForward = Vector3.forward;

        [Tooltip("车道中心线 x（训练场 3 条练习道；主路 4 条车道）")]
        public float[] laneCenterXs = { -3.6f, 0f, 3.6f };

        public float laneWidthM = 3.6f;

        [Tooltip("几何复杂区中心 z（训练场＝直角弯区；主路＝路口）")]
        public float crossCenterZ = 112f;

        [Tooltip("几何复杂区沿主路的半宽（z 方向）")]
        public float crossHalfExtentM = 12f;

        [Tooltip("车头与主路夹角超过它 → 横向偏移判据失效（转弯中不判）")]
        public float headingToleranceDeg = 20f;

        /// <summary>离给定 x 最近的那条车道中心。</summary>
        public float NearestLaneCenterX(float x)
        {
            if (laneCenterXs == null || laneCenterXs.Length == 0) return 0f;

            float best = laneCenterXs[0];
            float bestDist = Mathf.Abs(x - best);
            for (int i = 1; i < laneCenterXs.Length; i++)
            {
                float d = Mathf.Abs(x - laneCenterXs[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = laneCenterXs[i];
                }
            }
            return best;
        }

        /// <summary>这个位置是否在路口（含一点余量）里。</summary>
        public bool IsInsideIntersection(Vector3 worldPos, float marginM = 0f)
            => Mathf.Abs(worldPos.z - crossCenterZ) <= crossHalfExtentM + marginM;

        /// <summary>车头是否还对正主路（转弯中就不算）。</summary>
        public bool IsHeadingAlongRoad(Transform target)
        {
            if (target == null) return false;
            float cosLimit = Mathf.Cos(headingToleranceDeg * Mathf.Deg2Rad);
            return Vector3.Dot(target.forward, roadForward.normalized) > cosLimit;
        }
    }
}
