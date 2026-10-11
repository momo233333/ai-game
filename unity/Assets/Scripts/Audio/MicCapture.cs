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
        [Tooltip("请求的采样率。**实际采样率以 AudioClip.frequency 为准** —— 设备不支持时 Unity 会就近取一个，"
            + "代码若按请求值当真去重采样，音高会整体偏移、ASR 直接崩。默认直接请求 16kHz（等于 ASR 输入率，无需重采样）。")]
        public int requestedRate = 16000;

        public int targetRate = 16000;
        [Range(2, 60)] public int maxSeconds = 20;

        [Header("设备")]
        [Tooltip("留空 = 系统默认设备。名字不用写全，包含匹配即可（见 Console 里打印的设备列表）。"
            + "⚠️ 若系统默认指向虚拟麦克风（如 Pico Streaming），识别会一塌糊涂，必须在这里指定真麦克风。")]
        public string deviceName = "";

        [Header("按键")]
        public bool pushToTalk = true;
        public KeyCode pttKey = KeyCode.Space;
        [Tooltip("非 PTT 模式：按一次开、再按一次关")]
        public bool toggleMode = false;

        [Header("抢话与尾巴")]
        [Tooltip("按下说话键时**打断妹妹的语音**（而不是静默丢弃你的话）。"
            + "开着才符合直觉：教练一开口，学员就该闭嘴。关掉则退回半双工（她说话时不录）。")]
        public bool bargeIn = true;
        [Tooltip("松开键后继续录一小段（毫秒）。人往往话说完了手先松，"
            + "最后几个字还在设备缓冲里 —— 实测「起步慢慢来」会丢成「起步慢慢」。")]
        [Range(0, 800)] public int releaseTailMs = 250;

        AudioClip _clip;
        int _readPos;
        /// <summary>本轮**实际**采集率（Begin 时从 <see cref="AudioClip.frequency"/> 读，不是请求值）。</summary>
        int _actualRate = 16000;
        readonly System.Collections.Generic.List<float> _captured = new System.Collections.Generic.List<float>(48000 * 10);
        bool _recording;
        bool _toggled;
        /// <summary>松开键的时刻（负值 = 未在收尾余量里）。</summary>
        float _releaseAt = -1f;

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

            // 妹妹说话期间：默认**抢话**（打断她并立刻录），否则退回半双工不上行。
            // 旧行为是直接 wantRecording=false —— 而她现在一句话要说 3~4 秒，
            // 玩家在这期间按空格说的话会被**整段丢掉**，实测就是「识别不到 / 只剩一两个字」的主因。
            if (wantRecording && ttsPlayer != null && ttsPlayer.IsSpeaking)
            {
                if (bargeIn)
                {
                    ttsPlayer.StopAll();
                    Debug.Log("[MicCapture] 抢话：打断妹妹的语音，开始录音");
                }
                else
                {
                    wantRecording = false;
                }
            }

            if (wantRecording)
            {
                _releaseAt = -1f;
                if (!_recording) Begin();
                else Pump();
                return;
            }

            if (!_recording) return;

            // 松开后的「尾巴余量」：补录一小段再收尾，避免最后几个字被切掉
            if (_releaseAt < 0f) _releaseAt = Time.unscaledTime;
            Pump();
            if (releaseTailMs > 0 && (Time.unscaledTime - _releaseAt) * 1000f < releaseTailMs) return;

            _releaseAt = -1f;
            End();
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
            _releaseAt = -1f;

            string device = ResolveDevice();
            _device = device;
            _clip = Microphone.Start(device, true, maxSeconds, requestedRate);
            if (_clip == null)
            {
                Debug.LogError("[MicCapture] Microphone.Start 返回 null，本轮无法录音");
                return;
            }

            // 关键：实际采样率只认 clip.frequency。设备/驱动不支持 requestedRate 时 Unity 会就近取，
            // 拿请求值去重采样会让整段音频音高偏移（例如 44100 当 48000 处理 → 慢 8.8%），ASR 基本必错。
            _actualRate = _clip.frequency > 0 ? _clip.frequency : requestedRate;

            _recording = true;
            Debug.Log(string.Format("[MicCapture] 开始录音：设备「{0}」实际 {1}Hz（请求 {2}Hz）· 可用设备 {3}",
                device ?? "系统默认", _actualRate, requestedRate, string.Join(" | ", Microphone.devices)));
            if (RecordingStarted != null) RecordingStarted();
        }

        /// <summary>按名字（包含匹配，不区分大小写）挑设备；没配或没匹配上就用系统默认。</summary>
        string ResolveDevice()
        {
            if (string.IsNullOrEmpty(deviceName)) return null;

            for (int i = 0; i < Microphone.devices.Length; i++)
            {
                string name = Microphone.devices[i];
                if (name != null && name.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0) return name;
            }

            Debug.LogWarning(string.Format("[MicCapture] 没找到含「{0}」的麦克风，改用系统默认。可用设备：{1}",
                deviceName, string.Join(" | ", Microphone.devices)));
            return null;
        }

        /// <summary>本轮实际用的设备名（null = 系统默认）。End 必须用同一个名字，否则停不掉。</summary>
        string _device;

        public void End()
        {
            if (!_recording) return;
            _recording = false;
            _releaseAt = -1f;
            Pump();

            if (Microphone.IsRecording(_device)) Microphone.End(_device);
            _clip = null;
            _device = null;

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
            int frames = atSourceRate.Length / 2;
            double seconds = _actualRate > 0 ? frames / (double)_actualRate : 0;

            // 电平取证：识别不准时先看这两个数 —— 峰值太小说明是「录得太小声」，不是模型的问题
            int peak = 0;
            long sumSq = 0;
            for (int i = 0; i < frames; i++)
            {
                short s = BitConverter.ToInt16(atSourceRate, i * 2);
                int a = s < 0 ? -s : s;
                if (a > peak) peak = a;
                sumSq += (long)s * s;
            }
            int rms = frames > 0 ? (int)Math.Sqrt(sumSq / (double)frames) : 0;

            Debug.Log(string.Format("[MicCapture] 录音 {0} 帧（{1:0.00}s @{2}Hz）峰值 {3} · RMS {4}{5}",
                frames, seconds, _actualRate, peak, rms,
                peak < 1500 ? "  ⚠️ 电平偏低（正常说话峰值应在 3000+），检查麦克风音量/距离" : ""));

            byte[] atTargetRate = _actualRate == targetRate
                ? atSourceRate
                : AudioUtil.Resample16(atSourceRate, _actualRate, targetRate);

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