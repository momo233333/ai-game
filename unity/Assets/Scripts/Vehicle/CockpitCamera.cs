using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 第一人称**副驾驶**视角（《驾考宝典》）。
    ///
    /// 与车外跟随镜头的区别：相机是 <c>Car</c> 的子物体，**位置就固定在副驾座上**，
    /// 随车身一起动；本脚本只负责「转头」——玩家可以扭头去看正在开车的学员
    /// （Q/E 或按住鼠标右键），松手自动回正到正前方。
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
        public float recenterSpeed = 130f;

        [Tooltip("回正时朝向的默认偏角（负 = 稍微偏向驾驶位那侧），松手会回到这里")]
        public float restYaw = 0f;

        [Header("鼠标")]
        [Tooltip("按住鼠标右键拖动也能转头")]
        public bool useMouse = true;
        public float mouseSensitivity = 3.5f;

        float _yaw;
        float _pitch;

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

            // 没有输入就自动回正（回到 restYaw，不是 0）
            if (Mathf.Approximately(keyDelta, 0f) && !mouseLooking)
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