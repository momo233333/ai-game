using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 跟随摄像机（方案 §1.2）：没有 Cinemachine 依赖，灰盒阶段够用。
    /// 位置用指数平滑跟，朝向始终看向车头前方一点，避免急转时甩镜。
    /// </summary>
    public sealed class ChaseCamera : MonoBehaviour
    {
        public Transform target;

        [Tooltip("相对目标的车体坐标偏移（后上方）")]
        public Vector3 offset = new Vector3(0f, 3.2f, -7.5f);

        [Tooltip("位置跟随速度（越大越跟手）")]
        public float followSharpness = 6f;

        [Tooltip("朝向跟随速度")]
        public float lookSharpness = 8f;

        [Tooltip("视线焦点相对车体的抬高量")]
        public float lookHeight = 1.2f;

        void LateUpdate()
        {
            if (target == null) return;

            Vector3 desired = target.TransformPoint(offset);
            float posT = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, posT);

            Vector3 focus = target.position + Vector3.up * lookHeight;
            Vector3 toFocus = focus - transform.position;
            if (toFocus.sqrMagnitude < 1e-4f) return;

            float lookT = 1f - Mathf.Exp(-lookSharpness * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(toFocus, Vector3.up), lookT);
        }
    }
}