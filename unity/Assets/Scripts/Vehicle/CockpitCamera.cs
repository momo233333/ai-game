using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 第一人称**副驾驶**视角（《驾考宝典》）。
    ///
    /// 与车外跟随镜头的区别：相机是 <c>Car</c> 的子物体，**位置就固定在副驾座上**，
    /// 随车身一起动；本脚本只负责「转头」——玩家可以扭头去看正在开车的学员
    /// （Q/E 或按住鼠标右键）。
    ///
    /// **转头不自动回正**（用户反馈「我个人视角的转向问题」）：原来一松手就 `MoveTowards(restYaw)` 把视线掰回正前方，
    /// 想一直侧头看学员就得一直按着键 —— 现在**转过就停在那儿**，只有玩家自己往回转才能转回来。
    /// </summary>
    public sealed class CockpitCamera : MonoBehaviour
    {
        [Header("转头")]
        public bool allowLook = true;
        public KeyCode lookLeftKey = KeyCode.Q;
        public KeyCode lookRightKey = KeyCode.E;
        [Tooltip("左右扭头上限（度）。副驾看驾驶位的学员，60~70 够用")]
        public float maxYaw = 68f;
        [Tooltip("上下抬头/低头上限（度）")]
        public float maxPitch = 22f;
        public float lookSpeed = 95f;

        [Tooltip("松手是否自动回正。**默认关**：转过就停在那儿（用户明确要求）")]
        public bool recenterOnRelease = false;
        [Tooltip("自动回正速度（只在 recenterOnRelease 打开时用）")]
        public float recenterSpeed = 130f;

        [Tooltip("初始朝向的偏角（负 = 稍微偏向驾驶位那侧）")]
        public float restYaw = 0f;

        [Header("鼠标")]
        [Tooltip("按住鼠标右键拖动也能转头")]
        public bool useMouse = true;
        public float mouseSensitivity = 3.5f;

        float _yaw;
        float _pitch;

        /// <summary>
        /// 起始朝向就用 <see cref="restYaw"/>（不再靠"第一帧自动回正"把它拉过去 —— 那条回正已经默认关掉了，
        /// 不在这里种一次的话，restYaw=-15° 这个"稍微偏向驾驶位"的开局视角会失效，变成正对着前方）。
        /// </summary>
        void Awake()
        {
            _yaw = restYaw;
            _pitch = 0f;
        }

        void Update()
        {
            if (!allowLook)
            {
                _yaw = restYaw;
                _pitch = 0f;
                Apply();
                return;
            }

            float keyDelta = 0f;
            if (Input.GetKey(lookLeftKey)) keyDelta -= 1f;
            if (Input.GetKey(lookRightKey)) keyDelta += 1f;
            _yaw += keyDelta * lookSpeed * Time.deltaTime;

            bool mouseLooking = useMouse && Input.GetMouseButton(1);
            if (mouseLooking)
            {
                _yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
            }

            // 没有输入时：默认**保持原样**（转过就停在那儿）；只有显式打开 recenterOnRelease 才回正
            if (recenterOnRelease && Mathf.Approximately(keyDelta, 0f) && !mouseLooking)
            {
                _yaw = Mathf.MoveTowards(_yaw, restYaw, recenterSpeed * Time.deltaTime);
                _pitch = Mathf.MoveTowards(_pitch, 0f, recenterSpeed * Time.deltaTime);
            }

            _yaw = Mathf.Clamp(_yaw, restYaw - maxYaw, restYaw + maxYaw);
            _pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);
            Apply();
        }

        void Apply()
        {
            transform.localRotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }
    }
}