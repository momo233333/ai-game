using Copilot.Audio;
using Copilot.Core;
using Copilot.State;
using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// 驾驶位上的学员（《驾考宝典》）——让副驾视角里「有人」而不是空车。
    ///
    /// 表现四件事（issue #2 的 ③，**只做低成本部分**：Blend Tree / IK 留到换真人物模型之后）：
    /// ① 双手跟着方向盘转（**指数平滑**，硬跟会有机械感）；
    /// ② 说话时扭头看教练 + 轻微点头 + **张嘴开合**；
    /// ③ 紧张（情绪 = NERVOUS）时头部有高频轻微抖动 —— 「我手心全是汗」得有地方看得见；
    /// ④ 沉默时所有量平滑回到静止姿态，不会突然"啪"一下复位。
    ///
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
        [Tooltip("嘴（说话时开合）；没接就只是不说话")]
        public Transform mouth;
        [Tooltip("情绪来源（紧张微动要它）；没接就不抖")]
        public GameStateManager gameState;

        [Header("手跟方向盘")]
        [Range(0f, 0.3f)] public float handFollow = 0.14f;
        [Tooltip("手臂追上方向盘的速度（0 = 硬跟，会有机械感）")]
        public float armFollowSpeed = 9f;

        [Header("说话时扭头看教练")]
        public TtsPlayer ttsPlayer;
        [Tooltip("看向教练时的头部偏转角（正数 = 朝副驾那侧）")]
        public float headTurnAngle = 30f;
        public float headTurnSpeed = 5f;

        [Header("说话：嘴 + 点头")]
        [Tooltip("嘴张到最大时的纵向倍数")]
        public float mouthOpenScale = 2.6f;
        [Tooltip("嘴开合频率（次/秒）")]
        public float mouthHz = 3.6f;
        [Tooltip("说话时点头幅度（度）")]
        public float talkNodDeg = 3.5f;

        [Header("紧张微动")]
        [Tooltip("NERVOUS 时头部抖动幅度（度）")]
        public float nervousShakeDeg = 1.6f;
        [Tooltip("紧张抖动的频率（次/秒）")]
        public float nervousShakeHz = 6.5f;

        [Header("预览 / 调试")]
        [Tooltip("强制当成「正在说话」，供编辑器预览图检查嘴型（编辑模式没有音频）")]
        public bool forceTalking;

        float _headYaw;
        float _handAngle;
        float _nod;
        Vector3 _mouthBaseScale = Vector3.zero;
        Vector3 _mouthBasePos = Vector3.zero;

        void Update()
        {
            float dt = Time.deltaTime;
            bool talking = forceTalking || (ttsPlayer != null && ttsPlayer.IsSpeaking);

            // ① 手跟方向盘：指数平滑。直接赋值是"盘一转手瞬移"，看着像木偶。
            float wheelAngle = wheel != null ? wheel.Angle : 0f;
            float targetHand = -wheelAngle * handFollow;
            _handAngle = Smooth(_handAngle, targetHand, armFollowSpeed, dt);
            if (leftArm != null) leftArm.localRotation = Quaternion.Euler(0f, 0f, _handAngle);
            if (rightArm != null) rightArm.localRotation = Quaternion.Euler(0f, 0f, _handAngle);

            // ② 说话 → 扭头看教练（不说话回正）
            _headYaw = Smooth(_headYaw, talking ? headTurnAngle : 0f, headTurnSpeed, dt);

            // ② 点头：说话时按 mouthHz 摆动，停下就平滑回中（不硬切）
            _nod = talking
                ? Mathf.Sin(Time.time * mouthHz * Mathf.PI * 2f) * talkNodDeg
                : Smooth(_nod, 0f, 6f, dt);

            // ③ 紧张微抖：幅度很小，只是让头部不"死"
            bool nervous = gameState != null && gameState.Emotion == Emotion.Nervous;
            float shake = nervous ? Mathf.Sin(Time.time * nervousShakeHz * Mathf.PI * 2f) * nervousShakeDeg : 0f;

            if (head != null) head.localRotation = Quaternion.Euler(_nod, _headYaw, shake);

            UpdateMouth(talking);
        }

        /// <summary>张嘴：纵向放大 + 下巴微沉。开合用 |sin|（嘴不会"负开"）。</summary>
        void UpdateMouth(bool talking)
        {
            if (mouth == null) return;

            // 引用可能是运行时注入的，所以基准值懒取（Awake 时可能还没接上）
            if (_mouthBaseScale == Vector3.zero)
            {
                _mouthBaseScale = mouth.localScale;
                _mouthBasePos = mouth.localPosition;
            }

            float open = talking ? Mathf.Abs(Mathf.Sin(Time.time * mouthHz * Mathf.PI)) : 0f;
            mouth.localScale = new Vector3(
                _mouthBaseScale.x * (1f - 0.25f * open),
                _mouthBaseScale.y * (1f + (mouthOpenScale - 1f) * open),
                _mouthBaseScale.z);
            mouth.localPosition = _mouthBasePos + new Vector3(0f, -0.006f * open, 0f);
        }

        /// <summary>帧率无关的指数逼近：speed 越大越"跟手"。</summary>
        static float Smooth(float current, float target, float speed, float dt)
            => speed <= 0f ? target : Mathf.Lerp(current, target, 1f - Mathf.Exp(-speed * dt));
    }
}
