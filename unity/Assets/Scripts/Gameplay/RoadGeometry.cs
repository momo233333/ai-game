using UnityEngine;

namespace Copilot.Gameplay
{
    /// <summary>
    /// 灰盒道路几何的**唯一真源**（挂在 <c>City</c> 上）。
    ///
    /// 车道中心/宽度/路口范围属于**场景制作数据**（和楼群坐标一个性质），不是游戏规则，
    /// 所以留在 Unity 侧；Core 只拿"离本车道中心偏了多少"这个测量结果做判定 ——
    /// 与 <c>VehicleTelemetry</c> 的分工一致。
    ///
    /// 数值来自 <c>SceneBuilder</c>：路面半宽 8、车道虚线在 x=±3.6、边线 x=±7.2
    /// → 车道中心 −5.4 / −1.8 / +1.8 / +5.4，车道宽 3.6m；路口中心 z=120、半宽 8m。
    /// </summary>
    public sealed class RoadGeometry : MonoBehaviour
    {
        [Tooltip("主路前进方向（灰盒里是 +Z）")]
        public Vector3 roadForward = Vector3.forward;

        [Tooltip("四条车道的中心线 x（从最左到最右）")]
        public float[] laneCenterXs = { -5.4f, -1.8f, 1.8f, 5.4f };

        public float laneWidthM = 3.6f;

        [Tooltip("路口中心 z")]
        public float crossCenterZ = 120f;

        [Tooltip("路口沿主路的半宽（z 方向）")]
        public float crossHalfExtentM = 8f;

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
