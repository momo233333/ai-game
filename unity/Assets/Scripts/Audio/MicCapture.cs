using System;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;

namespace Copilot.Audio
{
    /// <summary>
    /// 麦克风上行（方案 §4.1/§5.3）：Unity 采集（48kHz float）→ 重采样 16kHz → PCM16 → VoiceTurn。
    ///
    /// **按键说话（默认空格）**：按住录、松开提交，与命令行版一致；
    /// **半双工**：妹妹说话期间不上行，规避 Windows AEC 依赖。
    ///
    /// 注意 Unity 的麦克风读法是 <c>AudioClip.GetData</c>（<c>Microphone.Start</c> 返回的 clip），
    /// 没有 <c>Microphone.GetData</c> 这种 API —— 这一点比旧版骨架修正过。
    /// </summary>
    public sealed class MicCapture : MonoBehaviour
    {
        public CopilotConnection connection;
        public TtsPlayer ttsPlayer;

        [Header("采集")]
        public int sourceRate = 48000;
        public int targetRate = 16000;
        [Range(2, 60)] public int maxSeconds = 20;

        [Header("按键")]
        public bool pushToTalk = true;
        public KeyCode pttKey = KeyCode.Space;
        [Tooltip("非 PTT 模式：按一次开、再按一次关")]
        public bool toggleMode = false;

        AudioClip _clip;
        int _readPos;
        readonly System.Collections.Generic.List<float> _captured = new System.Collections.Generic.List<float>(48000 * 10);
        bool _recording;
        bool _toggled;

        public bool IsRecording
        {
            get { return _recording; }
        }

        public event Action RecordingStarted;
        public event Action<int> RecordingStopped;

        void Update()
        {
            bool wantRecording;
            if (pushToTalk)
            {
                wantRecording = Input.GetKey(pttKey);
            }
            else if (toggleMode)
            {
                if (Input.GetKeyDown(pttKey)) _toggled = !_toggled;
                wantRecording = _toggled;
            }
            else
            {
                wantRecording = _recording;
            }

            // 半双工：妹妹说话期间不上行，否则会把 TTS 回放录进去
            if (wantRecording && ttsPlayer != null && ttsPlayer.IsSpeaking) wantRecording = false;

            if (wantRecording && !_recording) Begin();
            else if (!wantRecording && _recording) End();
            else if (_recording) Pump();
        }

        public void Begin()
        {
            if (_recording) return;
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[MicCapture] 无可用麦克风");
                return;
            }

            _readPos = 0;
            _captured.Clear();
            _clip = Microphone.Start(null, true, maxSeconds, sourceRate);
            _recording = true;
            if (RecordingStarted != null) RecordingStarted();
        }

        public void End()
        {
            if (!_recording) return;
            _recording = false;
            Pump();

            if (Microphone.IsRecording(null)) Microphone.End(null);
            _clip = null;

            int samples = _captured.Count;
            if (RecordingStopped != null) RecordingStopped(samples);
            if (samples > 0) Submit();
            _captured.Clear();
        }

        /// <summary>把麦克风环形缓冲区里「上次读到的位置之后」的采样搬进来。</summary>
        void Pump()
        {
            if (_clip == null) return;

            int writePos = Microphone.GetPosition(null);
            if (writePos <= _readPos) return;

            int count = writePos - _readPos;
            var block = new float[count];
            if (!_clip.GetData(block, _readPos)) return;

            _captured.AddRange(block);
            _readPos = writePos;
        }

        void Submit()
        {
            if (connection == null || !connection.IsConnected)
            {
                Debug.LogWarning("[MicCapture] 未连接 AI 服务，丢弃录音");
                return;
            }

            byte[] atSourceRate = AudioUtil.ToPcm16(_captured.ToArray());
            byte[] atTargetRate = AudioUtil.Resample16(atSourceRate, sourceRate, targetRate);

            if (!connection.SendVoice(atTargetRate))
            {
                Debug.LogWarning("[MicCapture] 语音回合提交失败");
            }
        }

        void OnDestroy()
        {
            if (_recording) End();
        }
    }
}