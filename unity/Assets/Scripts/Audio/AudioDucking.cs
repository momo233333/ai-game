using UnityEngine;

namespace Copilot.Audio
{
    /// <summary>
    /// 语音说话时把 SFX/BGM 压 -6dB（方案 §5.3 三总线 ducking 的最小实现）。
    /// 挂在需要被压的 AudioSource 上，TtsPlayer.SpeakingChanged 驱动。
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class AudioDucking : MonoBehaviour
    {
        public TtsPlayer ttsPlayer;
        [Range(0f, 1f)] public float duckAmount = 0.5f;
        [Range(1f, 30f)] public float blendSpeed = 8f;

        AudioSource _source;
        float _original = 1f;
        float _target = 1f;

        void Awake()
        {
            _source = GetComponent<AudioSource>();
            _original = _source.volume <= 0f ? 1f : _source.volume;
        }

        void OnEnable()
        {
            if (ttsPlayer != null) ttsPlayer.SpeakingChanged += OnSpeakingChanged;
        }

        void OnDisable()
        {
            if (ttsPlayer != null) ttsPlayer.SpeakingChanged -= OnSpeakingChanged;
            _target = 1f;
        }

        void OnSpeakingChanged(bool speaking)
        {
            _target = speaking ? Mathf.Clamp01(1f - duckAmount) : 1f;
        }

        void Update()
        {
            if (_source == null) return;
            float desired = _original * _target;
            if (Mathf.Approximately(_source.volume, desired)) return;
            _source.volume = Mathf.MoveTowards(_source.volume, desired, Time.deltaTime * blendSpeed * _original);
        }
    }
}
