using System;
using System.Collections.Generic;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;

namespace Copilot.Audio
{
    /// <summary>
    /// TTS 下行播放（方案 §5.3）：<c>tts_audio</c> 事件 → 24kHz 裸 PCM16 → AudioClip 队列串行播放。
    /// 半双工：说话期间 <see cref="IsSpeaking"/> 为 true，MicCapture 与 BGM/SFX ducking 订阅它。
    ///
    /// 没有音频（语音库未命中且实时 TTS 未装）时**只出字幕不出声**，字幕与音频同源（都用事件里的 text）。
    /// </summary>
    public sealed class TtsPlayer : MonoBehaviour
    {
        public CopilotConnection connection;
        [Tooltip("留空则挂在同一物体上的 AudioSource")]
        public AudioSource source;

        [Header("播放")]
        public bool playAudio = true;
        [Range(0f, 1f)] public float voiceVolume = 1f;

        sealed class Line
        {
            public float[] Samples;
            public int SampleRate;
            public string Text;
        }

        readonly Queue<Line> _pending = new Queue<Line>();
        bool _playing;
        string _currentText = "";

        public bool IsSpeaking
        {
            get { return _playing; }
        }

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

        CopilotConnection _bound;

        void OnEnable()
        {
            Rebind();
        }

        /// <summary>幂等绑定（AddComponent 会在字段注入前触发 OnEnable，装配方注入后再调一次）。</summary>
        public void Rebind()
        {
            if (_bound == connection) return;
            if (_bound != null) _bound.TurnEventReceived -= HandleTurnEvent;
            _bound = connection;
            if (_bound != null) _bound.TurnEventReceived += HandleTurnEvent;
        }

        void OnDisable()
        {
            if (_bound != null)
            {
                _bound.TurnEventReceived -= HandleTurnEvent;
                _bound = null;
            }
            StopAll();
        }

        void Start()
        {
            Rebind();
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null || evt.Kind != TurnEventKind.TtsAudio) return;

            float[] samples = evt.Pcm16 != null && evt.Pcm16.Length > 0
                ? AudioUtil.ToFloat(evt.Pcm16)
                : null;

            if (samples == null || samples.Length == 0)
            {
                // 没有音频也要把字幕推出去，否则「妹妹不吭声也不上屏」
                if (!string.IsNullOrEmpty(evt.Text))
                {
                    if (LineStarted != null) LineStarted(evt.Text);
                    if (LineCompleted != null) LineCompleted(evt.Text);
                }
                return;
            }

            lock (_pending)
            {
                _pending.Enqueue(new Line
                {
                    Samples = samples,
                    SampleRate = evt.SampleRate > 0 ? evt.SampleRate : 24000,
                    Text = evt.Text,
                });
            }
            if (LineStarted != null) LineStarted(evt.Text);
        }

        void Update()
        {
            if (_playing) return;

            Line next = null;
            lock (_pending)
            {
                if (_pending.Count > 0) next = _pending.Dequeue();
            }
            if (next != null) Play(next);
        }

        void Play(Line line)
        {
            if (!playAudio || source == null)
            {
                Finish(line);
                return;
            }

            var clip = AudioClip.Create("tts", line.Samples.Length, 1, line.SampleRate, false);
            clip.SetData(line.Samples, 0);

            source.clip = clip;
            source.volume = voiceVolume;
            source.Play();
            _currentText = line.Text;
            _playing = true;
            SetSpeaking(true);
        }

        void LateUpdate()
        {
            if (!_playing) return;
            if (source != null && source.isPlaying) return;

            string text = _currentText;
            _currentText = "";
            _playing = false;

            var clip = source != null ? source.clip : null;
            if (source != null) source.clip = null;
            if (clip != null) Destroy(clip);

            SetSpeaking(false);
            if (LineCompleted != null) LineCompleted(text);
        }

        void Finish(Line line)
        {
            if (LineCompleted != null) LineCompleted(line != null ? line.Text : "");
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
            _currentText = "";
            SetSpeaking(false);
        }
    }
}