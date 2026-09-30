// 仅供无头编译检查使用的 UnityEngine 最小桩 —— 不参与游戏运行。
// 真实 Unity 下由引擎提供实现；本桩只保证 API 形状/签名与用法一致。
using System;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static void Destroy(Object obj) { }
        public static void DestroyImmediate(Object obj) { }
    }

    public class GameObject : Object
    {
        public T AddComponent<T>() where T : Component, new() { return new T(); }
        public T GetComponent<T>() { return default(T); }
    }

    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform = new Transform();
        public T GetComponent<T>() { return default(T); }
    }

    public class Behaviour : Component
    {
        public bool enabled;
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() { return new T(); }
    }

    public class Transform : Component
    {
        public Vector3 position;
        public Vector3 forward = new Vector3(0, 0, 1);
        public Vector3 up = new Vector3(0, 1, 0);
        public void SetPositionAndRotation(Vector3 pos, Quaternion rot) { position = pos; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public float magnitude { get { return (float)Math.Sqrt(x * x + y * y + z * z); } }
        public static Vector3 operator *(Vector3 v, float m) { return new Vector3(v.x * m, v.y * m, v.z * m); }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
    }

    public struct Quaternion
    {
        public static Quaternion Euler(float x, float y, float z) { return new Quaternion(); }
        public static Vector3 operator *(Quaternion q, Vector3 v) { return v; }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) : this(r, g, b, 1f) { }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white { get { return new Color(1, 1, 1); } }
        public static Color cyan { get { return new Color(0, 1, 1); } }
        public static Color red { get { return new Color(1, 0, 0); } }
    }

    public static class Mathf
    {
        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Clamp01(float v) { return Clamp(v, 0f, 1f); }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static int Max(int a, int b) { return a > b ? a : b; }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static int Min(int a, int b) { return a < b ? a : b; }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
        public static float MoveTowards(float cur, float target, float delta)
        {
            if (Math.Abs(target - cur) <= delta) return target;
            return cur + Math.Sign(target - cur) * delta;
        }
        public static bool Approximately(float a, float b) { return Math.Abs(a - b) < 1e-5f; }
        public static int FloorToInt(float v) { return (int)Math.Floor(v); }
        public static int RoundToInt(float v) { return (int)Math.Round(v); }
        public static float Sign(float v) { return v >= 0f ? 1f : -1f; }
    }

    public static class Time
    {
        public static float deltaTime { get { return 0.016f; } }
        public static float fixedDeltaTime { get { return 0.02f; } }
        public static float time { get { return 0f; } }
    }

    public static class Application
    {
        public static bool isPlaying { get { return false; } }
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception) { }
    }

    public static class Gizmos
    {
        public static Color color { get; set; }
        public static void DrawRay(Vector3 from, Vector3 direction) { }
    }

    public class Rigidbody : Component
    {
        public Vector3 velocity;
        public Vector3 centerOfMass;
    }

    public class WheelCollider : Component
    {
        public float steerAngle { get; set; }
        public float motorTorque { get; set; }
        public float brakeTorque { get; set; }
        public void GetWorldPose(out Vector3 position, out Quaternion rotation)
        {
            position = new Vector3();
            rotation = new Quaternion();
        }
    }

    public class AudioClip : Object
    {
        public static AudioClip Create(string name, int lengthSamples, int channels, int frequency, bool stream)
        {
            return new AudioClip();
        }
        public bool SetData(float[] data, int offsetSamples) { return true; }
    }

    public class AudioSource : Behaviour
    {
        public AudioClip clip { get; set; }
        public bool playOnAwake { get; set; }
        public bool loop { get; set; }
        public float spatialBlend { get; set; }
        public float volume { get; set; }
        public bool isPlaying { get; set; }
        public void Play() { isPlaying = true; }
        public void Stop() { isPlaying = false; }
    }

    public static class Microphone
    {
        public static bool IsAvailable(string deviceName) { return true; }
        public static string Start(string deviceName, bool loop, int lengthSec, int frequency) { return deviceName; }
        public static void End(string deviceName) { }
        public static int GetPosition(string deviceName) { return 0; }
        public static float[] GetData(string deviceName, int offsetSamples, int length) { return new float[length]; }
    }

    public enum KeyCode
    {
        None = 0,
        Return = 13,
        KeypadEnter = 271,
        V = 118,
        Space = 32,
    }

    public static class Input
    {
        public static bool GetKey(KeyCode key) { return false; }
        public static bool GetKeyDown(KeyCode key) { return false; }
    }

    public class AnimationCurve
    {
        public int length { get { return 1; } }
        public float Evaluate(float time) { return time; }
        public static AnimationCurve EaseInOut(float timeStart, float valueStart, float timeEnd, float valueEnd)
        {
            return new AnimationCurve();
        }
    }

    public class Sprite : Object
    {
    }

    // ---------- 属性 ----------

    public sealed class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header) { }
    }

    public sealed class TooltipAttribute : Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }

    public sealed class RangeAttribute : Attribute
    {
        public RangeAttribute(float min, float max) { }
    }

    public sealed class RequireComponent : Attribute
    {
        public RequireComponent(Type requiredComponent) { }
    }

    public sealed class CreateAssetMenuAttribute : Attribute
    {
        public string menuName { get; set; }
        public string fileName { get; set; }
    }

    public sealed class DefaultExecutionOrder : Attribute
    {
        public DefaultExecutionOrder(int order) { }
    }

    public sealed class SerializeFieldAttribute : Attribute
    {
    }
}

namespace UnityEngine.Events
{
    public class UnityEvent
    {
        public void AddListener(Action callback) { }
        public void RemoveListener(Action callback) { }
        public void Invoke() { }
    }
}

namespace UnityEngine.UI
{
    public class Graphic : MonoBehaviour
    {
        public Color color { get; set; }
    }

    public class Text : Graphic
    {
        public string text { get; set; }
    }

    public class Image : Graphic
    {
        public Sprite sprite { get; set; }
        public float fillAmount { get; set; }
    }

    public class InputField : MonoBehaviour
    {
        public string text { get; set; }
        public bool isFocused { get; set; }
    }

    public class Button : MonoBehaviour
    {
        public Events.UnityEvent onClick = new Events.UnityEvent();
    }
}
