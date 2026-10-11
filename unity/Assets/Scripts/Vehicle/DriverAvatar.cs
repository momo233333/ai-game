using Copilot.Audio;
using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 驾驶位上的学员（《驾考宝典》）——让副驾视角里「有人」而不是空车。
    ///
    /// 两件事：① 双手跟着方向盘转；② 妹妹说话时把头扭过来看教练（不说话就看前方）。
    /// 纯表现，不参与物理；真人物模型到位后替换其子物体即可，脚本接口不用改。
    /// </summary>
    public sealed class DriverAvatar : MonoBehaviour
    {
        [Header("挂点")]
        public SteeringWheel wheel;
        public Transform head;
        [Tooltip("两只前臂（绕 Z 跟方向盘转）")]
        public Transform leftArm;
        public Transform rightArm;

        [Header("手跟方向盘")]
        [Range(0f, 0.3f)] public float handFollow = 0.14f;

        [Header("说话时扭头看教练")]
        public TtsPlayer ttsPlayer;
        [Tooltip("看向教练时的头部偏转角（正数 = 朝副驾那侧）")]
        public float headTurnAngle = 30f;
        public float headTurnSpeed = 5f;

        float _headYaw;

        void Update()
        {
            float wheelAngle = wheel != null ? wheel.Angle : 0f;
            float handAngle = -wheelAngle * handFollow;
            if (leftArm != null) leftArm.localRotation = Quaternion.Euler(0f, 0f, handAngle);
            if (rightArm != null) rightArm.localRotation = Quaternion.Euler(0f, 0f, handAngle);

            bool lookAtCoach = ttsPlayer != null && ttsPlayer.IsSpeaking;
            float targetYaw = lookAtCoach ? headTurnAngle : 0f;
            _headYaw = Mathf.Lerp(_headYaw, targetYaw, 1f - Mathf.Exp(-headTurnSpeed * Time.deltaTime));
            if (head != null) head.localRotation = Quaternion.Euler(0f, _headYaw, 0f);
        }
    }
}