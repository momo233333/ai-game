using System;
using System.Collections.Generic;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;

namespace Copilot.Audio
{
    /// <summary>
    /// TTS 下行播放（方案 §5.3）：tts_audio 事件 → PCM16 → AudioClip 队列串行播放。
    /// 半双工：说话期间 IsSpeaking=true，上行采集与 BGM/SFX ducking 订阅它。
    /// </summary>
    public sealed class TtsPlayer : MonoBehaviour
    {
        public AIClient client;
        [Tooltip("留空则挂在同一物体上的 AudioSource")]
        public AudioSource source;

        [Header("播放")]
        public bool playAudio = true;
        [Tooltip("语音总线音量（0..1）")]
        [Range(0f, 1f)] public float voiceVolume = 1f;

        readonly Queue<PcmAudio.Chunk> _pending = new Queue<PcmAudio.Chunk>();
        bool _playing;

        public bool IsSpeaking
        {
            get { return _playing; }
        }

        /// <summary>一句话开始 / 结束（UI 打字机与 ducking 订阅）。</summary>
        public event Action<string> LineStarted;
        public event Action<string> LineCompleted;
        public event Action<bool> SpeakingChanged;

        void Awake()
        {
            if (source == null) source = GetComponent<AudioSource>();
            if (source == null) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
        }

        AIClient _boundClient;

        void OnEnable()
        {
            Rebind();
        }

        /// <summary>幂等绑定（AddComponent 会在字段注入前触发 OnEnable，装配方注入后再调一次）。</summary>
        public void Rebind()
        {
            if (_boundClient == client) return;
            if (_boundClient != null) _boundClient.TurnEventReceived -= HandleTurnEvent;
            _boundClient = client;
            if (_boundClient != null) _boundClient.TurnEventReceived += HandleTurnEvent;
        }

        void OnDisable()
        {
            if (_boundClient != null)
            {
                _boundClient.TurnEventReceived -= HandleTurnEvent;
                _boundClient = null;
            }
            if (source != null) source.Stop();
            lock (_pending) _pending.Clear();
            SetSpeaking(false);
        }

        void Start()
        {
            Rebind();
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null || evt.Kind != "tts_audio") return;
            var chunk = PcmAudio.FromEvent(evt);
            string text = Copilot.Core.SimpleJson.GetString(evt.Data, "text");

            // 没有音频资产时（语音库未生成 wav / 实时 TTS 未装）也要把字幕推出去
            if (chunk == null || chunk.Samples == null || chunk.Samples.Length == 0)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    if (LineStarted != null) LineStarted(text);
                    if (LineCompleted != null) LineCompleted(text);
                }
                return;
            }

            lock (_pending) _pending.Enqueue(chunk);
            if (LineStarted != null) LineStarted(text);
        }

        void Update()
        {
            if (_playing) return;
            PcmAudio.Chunk next = null;
            lock (_pending)
            {
                if (_pending.Count > 0) next = _pending.Dequeue();
            }
            if (next != null) Play(next);
        }

        void Play(PcmAudio.Chunk chunk)
        {
            if (!playAudio || source == null || chunk.Samples.Length == 0)
            {
                Finish(chunk);
                return;
            }

            int rate = chunk.SampleRate > 0 ? chunk.SampleRate : 24000;
            int channels = chunk.Channels > 0 ? chunk.Channels : 1;
            var clip = AudioClip.Create("tts_" + chunk.Samples.Length, chunk.Samples.Length, channels, rate, false);
            clip.SetData(chunk.Samples, 0);

            source.clip = clip;
            source.volume = voiceVolume;
            source.Play();
            SetSpeaking(true);
            _playing = true;
            _currentText = chunk.Text;
        }

        string _currentText = "";

        void LateUpdate()
        {
            if (!_playing) return;
            if (source != null && source.isPlaying) return;
            string text = _currentText;
            _playing = false;
            _currentText = "";
            var clip = source != null ? source.clip : null;
            if (source != null) source.clip = null;
            if (clip != null) Destroy(clip);
            SetSpeaking(false);
            if (LineCompleted != null) LineCompleted(text);
        }

        void Finish(PcmAudio.Chunk chunk)
        {
            string text = chunk != null ? chunk.Text : "";
            if (LineCompleted != null) LineCompleted(text);
        }

        void SetSpeaking(bool speaking)
        {
            if (SpeakingChanged != null) SpeakingChanged(speaking);
        }

        public void StopAll()
        {
            lock (_pending) _pending.Clear();
            if (source != null) source.Stop();
            _playing = false;
            SetSpeaking(false);
        }
    }
}
