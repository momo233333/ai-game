using System;
using System.Buffers.Binary;

namespace Copilot.Core
{
    /// <summary>
    /// 语音活动检测（断句）抽象。
    /// 首选 Silero（sherpa-onnx ONNX）；无模型时降级到 <see cref="EnergyVad"/>（能量阈值），
    /// 保证「说完停顿就收尾」的交互在零模型环境下也可用。
    /// </summary>
    public interface IVad
    {
        string Name { get; }
        bool IsAvailable { get; }

        /// <summary>喂一帧 16kHz 单声道 16-bit PCM，返回这一帧是否判为语音。</summary>
        bool Accept(byte[] pcm16);

        /// <summary>已经说过话、且随后的静音超过阈值 → 可以收尾本句。</summary>
        bool SilenceExceeded { get; }

        /// <summary>重置到「还没开始说话」的状态，准备下一句。</summary>
        void Reset();
    }

    /// <summary>
    /// 能量阈值 VAD（零依赖兜底，也是可单测的纯逻辑）。
    /// 计算每帧 RMS，超过阈值算语音；语音之后连续静音超过 <c>minSilenceMs</c> 判定说完。
    /// </summary>
    public sealed class EnergyVad : IVad
    {
        private readonly float _thresholdRms;
        private readonly long _minSilenceSamples;
        private readonly int _sampleRate;

        private bool _sawSpeech;
        private long _silentSamples;
        private long _noiseFloor;

        public string Name => "energy";
        public bool IsAvailable => true;

        public EnergyVad(int sampleRate = 16000, float thresholdRms = 400f, double minSilenceSeconds = 0.6)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _sampleRate = sampleRate;
            _thresholdRms = thresholdRms < 1f ? 1f : thresholdRms;
            _minSilenceSamples = (long)(Math.Max(0.1, minSilenceSeconds) * sampleRate);
        }

        public bool Accept(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length < 2) return false;

            float rms = Rms(pcm16);
            bool speech = rms >= _thresholdRms;

            if (speech)
            {
                _sawSpeech = true;
                _silentSamples = 0;
            }
            else if (_sawSpeech)
            {
                _silentSamples += pcm16.Length / 2;
            }

            _noiseFloor = _noiseFloor == 0 ? (long)rms : (_noiseFloor * 9 + (long)rms) / 10;
            return speech;
        }

        public bool SilenceExceeded => _sawSpeech && _silentSamples >= _minSilenceSamples;

        public void Reset()
        {
            _sawSpeech = false;
            _silentSamples = 0;
        }

        /// <summary>最近的平均底噪（诊断用，调阈值时可参考）。</summary>
        public long NoiseFloor => _noiseFloor;

        public static float Rms(byte[] pcm16)
        {
            int frames = pcm16.Length / 2;
            if (frames == 0) return 0f;

            double sum = 0;
            for (int i = 0; i < frames; i++)
            {
                short s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
                sum += (double)s * s;
            }
            return (float)Math.Sqrt(sum / frames);
        }
    }
}