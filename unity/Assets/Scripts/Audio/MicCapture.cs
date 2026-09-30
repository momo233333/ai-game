using System;
using System.Collections.Generic;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;

namespace Copilot.Audio
{
    /// <summary>
    /// 麦克风上行（方案 §4.1/§5.3）：Unity 侧采集 → 重采样 16kHz → audio_turn 推给服务端。
    /// PTT 按住录音、松开提交；半双工——妹妹说话时不上行，规避 Windows AEC 依赖。
    /// </summary>
    public sealed class MicCapture : MonoBehaviour
    {
        public AIClient client;
        public TtsPlayer ttsPlayer;

        [Header("采集")]
        public int sourceRate = 48000;
        public int targetRate = 16000;
        [Range(2, 60)] public int maxSeconds = 20;
        [Tooltip("上行分块（毫秒）；方案建议 20ms/块，JSON-TCP 下取 100ms 兼顾体积")]
        [Range(20, 500)] public int blockMs = 100;

        [Header("按键")]
        public bool pushToTalk = true;
        public KeyCode pttKey = KeyCode.V;
        [Tooltip("非 PTT 模式：按一次开、再按一次关")]
        public bool toggleMode = false;

        float[] _buffer;
        int _readPos;
        readonly List<float> _captured = new List<float>(48000 * 10);
        bool _recording;
        readonly string _device = null;

        public bool IsRecording
        {
            get { return _recording; }
        }

        /// <summary>开始 / 结束录音（UI 状态灯订阅）。</summary>
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
                if (Input.GetKeyDown(pttKey)) _recording = !_recording;
                wantRecording = _recording;
            }
            else
            {
                wantRecording = _recording;
            }

            // 半双工：妹妹说话期间不上行
            if (wantRecording && ttsPlayer != null && ttsPlayer.IsSpeaking) wantRecording = false;

            if (wantRecording && !_recording) Begin();
            else if (!wantRecording && _recording) End();
            else if (_recording) Pump();
        }

        public void Begin()
        {
            if (_recording) return;
            if (!Microphone.IsAvailable(null))
            {
                Debug.LogWarning("[MicCapture] 无可用麦克风");
                return;
            }
            _buffer = new float[sourceRate * maxSeconds];
            _readPos = 0;
            _captured.Clear();
            Microphone.Start(_device, true, maxSeconds, sourceRate);
            _recording = true;
            if (RecordingStarted != null) RecordingStarted();
        }

        public void End()
        {
            if (!_recording) return;
            _recording = false;
            Pump();
            Microphone.End(_device);

            int samples = _captured.Count;
            if (RecordingStopped != null) RecordingStopped(samples);
            if (samples > 0) Submit();
            _captured.Clear();
            _buffer = null;
        }

        void Pump()
        {
            if (_buffer == null) return;
            int writePos = Microphone.GetPosition(_device);
            if (writePos < 0 || writePos >= _buffer.Length) return;

            int available = writePos - _readPos;
            if (available < 0) available += _buffer.Length;
            if (available <= 0) return;

            while (available > 0)
            {
                int segLen = Math.Min(available, _buffer.Length - _readPos);
                float[] segment = Microphone.GetData(_device, _readPos, segLen);
                if (segment == null || segment.Length == 0) break;
                _captured.AddRange(segment);
                available -= segment.Length;
                _readPos = (_readPos + segment.Length) % _buffer.Length;
            }
        }

        void Submit()
        {
            if (client == null || !client.IsConnected)
            {
                Debug.LogWarning("[MicCapture] 未连接 AI 服务，丢弃录音");
                return;
            }

            float[] downsampled = PcmAudio.Downsample(_captured.ToArray(), sourceRate, targetRate);
            byte[] pcm = PcmAudio.ToPcm16(downsampled);

            int blockSamples = Math.Max(1, targetRate * blockMs / 1000);
            int blockBytes = blockSamples * 2;
            var chunks = new List<string>();
            for (int offset = 0; offset < pcm.Length; offset += blockBytes)
            {
                int length = Math.Min(blockBytes, pcm.Length - offset);
                var slice = new byte[length];
                Array.Copy(pcm, offset, slice, 0, length);
                chunks.Add(Convert.ToBase64String(slice));
            }

            bool sent = client.Send(new Dictionary<string, object>
            {
                { "type", "audio_turn" },
                { "chunks", chunks },
            });
            if (!sent) Debug.LogWarning("[MicCapture] audio_turn 发送失败");
        }

        void OnDestroy()
        {
            if (_recording) End();
        }
    }
}
