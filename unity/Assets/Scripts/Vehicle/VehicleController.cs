using UnityEngine;

namespace Copilot.Vehicle
{
    /// <summary>
    /// WheelCollider 车辆控制（方案 §5.1）：前轮转向、后轮驱动、四轮制动。
    /// 只接受归一化输入（steering ∈ [-0.8,0.8]、throttle/brake ∈ [0,1]），
    /// 物理细节全部走 VehiclePhysicsProfile。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class VehicleController : MonoBehaviour
    {
        [Header("车轮（前 / 后）")]
        public WheelCollider wheelFrontLeft;
        public WheelCollider wheelFrontRight;
        public WheelCollider wheelRearLeft;
        public WheelCollider wheelRearRight;

        [Header("轮网可视")]
        public Transform meshFrontLeft;
        public Transform meshFrontRight;
        public Transform meshRearLeft;
        public Transform meshRearRight;

        [Header("配置")]
        public VehiclePhysicsProfile profile;

        [Header("倒车")]
        [Tooltip("倒车最高速度（倒车不该飚起来）")]
        public float reverseMaxSpeedKmh = 30f;

        Rigidbody _body;
        float _steer;
        float _throttle;
        float _brake;
        Vector3 _startPosition;
        Quaternion _startRotation;

        public float CurrentSteer
        {
            get { return _steer; }
        }

        public float CurrentThrottle
        {
            get { return _throttle; }
        }

        public float CurrentBrake
        {
            get { return _brake; }
        }

        public float SpeedKmh
        {
            get { return _body != null ? _body.velocity.magnitude * 3.6f : 0f; }
        }

        /// <summary>简化档位显示（不模拟变速箱）。</summary>
        public int Gear
        {
            get
            {
                float speed = SpeedKmh;
                if (speed < 1f) return 0;
                if (speed < 25f) return 1;
                if (speed < 50f) return 2;
                if (speed < 80f) return 3;
                if (speed < 110f) return 4;
                return 5;
            }
        }

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            if (_body != null)
            {
                _body.centerOfMass = new Vector3(0f, -0.35f, 0f);
            }

            // 记下起始位姿，供重开一局时归位
            _startPosition = transform.position;
            _startRotation = transform.rotation;
        }

        /// <summary>
        /// 重开一局：车归位并把速度清零 —— 否则会带着上一局的惯性冲出去。
        /// </summary>
        public void ResetToStart()
        {
            ClearInput();
            transform.SetPositionAndRotation(_startPosition, _startRotation);
            if (_body == null) return;
            _body.velocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }

        /// <summary>ActionExecutor 每 FixedUpdate 调用一次。<paramref name="throttle"/> 为负表示**倒车**。</summary>
        public void ApplyInput(float steering, float throttle, float brake)
        {
            _steer = Mathf.Clamp(steering, -1f, 1f);
            _throttle = Mathf.Clamp(throttle, -1f, 1f);   // 负 = 倒车（见 Intents.Reverse）
            _brake = Mathf.Clamp01(brake);

            float maxSteer = profile != null ? profile.maxSteerAngle : 32f;
            float motor = profile != null ? profile.motorTorque : 1400f;
            float brakeMax = profile != null ? profile.brakeTorque : 3500f;

            float targetAngle = _steer * maxSteer;
            if (wheelFrontLeft != null) wheelFrontLeft.steerAngle = targetAngle;
            if (wheelFrontRight != null) wheelFrontRight.steerAngle = targetAngle;

            float speedKmh = SpeedKmh;
            bool reversing = _throttle < 0f;
            float maxSpeed = reversing
                ? reverseMaxSpeedKmh
                : (profile != null ? profile.maxSpeedKmh : 140f);
            float speedFactor = speedKmh >= maxSpeed ? 0f : 1f;

            float magnitude = Evaluate(profile != null ? profile.throttleCurve : null, Mathf.Abs(_throttle))
                              * motor * speedFactor;
            float torque = reversing ? -magnitude : magnitude;
            if (wheelRearLeft != null) wheelRearLeft.motorTorque = torque;
            if (wheelRearRight != null) wheelRearRight.motorTorque = torque;

            float brakeForce = Evaluate(profile != null ? profile.brakeCurve : null, _brake) * brakeMax;
            ApplyBrake(wheelFrontLeft, brakeForce);
            ApplyBrake(wheelFrontRight, brakeForce);
            ApplyBrake(wheelRearLeft, brakeForce);
            ApplyBrake(wheelRearRight, brakeForce);
        }

        /// <summary>玩家接管 / 熄火：四轮回中位。</summary>
        public void ClearInput()
        {
            ApplyInput(0f, 0f, 0f);
        }

        void FixedUpdate()
        {
            SyncWheelMesh(wheelFrontLeft, meshFrontLeft);
            SyncWheelMesh(wheelFrontRight, meshFrontRight);
            SyncWheelMesh(wheelRearLeft, meshRearLeft);
            SyncWheelMesh(wheelRearRight, meshRearRight);
        }

        static void ApplyBrake(WheelCollider wheel, float force)
        {
            if (wheel != null) wheel.brakeTorque = force;
        }

        static float Evaluate(AnimationCurve curve, float value)
        {
            if (curve == null || curve.length == 0) return value;
            return Mathf.Clamp01(curve.Evaluate(Mathf.Clamp01(value)));
        }

        static void SyncWheelMesh(WheelCollider wheel, Transform mesh)
        {
            if (wheel == null || mesh == null) return;
            Vector3 position;
            Quaternion rotation;
            wheel.GetWorldPose(out position, out rotation);
            mesh.SetPositionAndRotation(position, rotation);
        }
    }
}
