using System;
using System.Buffers.Binary;
using System.IO;

namespace Copilot.Core
{
    /// <summary>
    /// 极简 WAV 解析：定位 "fmt " / "data" 块，输出单声道 16-bit 裸 PCM。
    /// 不依赖 System.Media / NAudio，Unity 与伴生进程共用（IT方案1.md V2.0 §5.5 音频契约）。
    /// </summary>
    public static class WavReader
    {
        public sealed class WavData
        {
            public byte[] Pcm16 { get; set; } = Array.Empty<byte>();
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int BitsPerSample { get; set; }
        }

        public static WavData Read(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                return Read(fs);
            }
        }

        public static WavData Read(Stream stream)
        {
            using (var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                if (br.ReadBytes(4) is var riff && !IsAscii(riff, "RIFF"))
                    throw new InvalidDataException("不是 WAV：缺少 RIFF 标识");
                br.ReadUInt32(); // riff size
                if (!IsAscii(br.ReadBytes(4), "WAVE"))
                    throw new InvalidDataException("不是 WAV：缺少 WAVE 标识");

                int sampleRate = 0, channels = 0, bits = 0;
                byte[]? data = null;

                while (stream.Position + 8 <= stream.Length)
                {
                    var chunkId = br.ReadBytes(4);
                    if (chunkId.Length < 4) break;
                    uint size = br.ReadUInt32();
                    long next = stream.Position + size;

                    if (IsAscii(chunkId, "fmt "))
                    {
                        br.ReadUInt16(); // audio format
                        channels = br.ReadUInt16();
                        sampleRate = (int)br.ReadUInt32();
                        br.ReadUInt32(); // byte rate
                        br.ReadUInt16(); // block align
                        bits = br.ReadUInt16();
                    }
                    else if (IsAscii(chunkId, "data"))
                    {
                        data = br.ReadBytes((int)Math.Min(size, stream.Length - stream.Position));
                    }

                    if (next > stream.Length) break;
                    stream.Position = next + (size % 2); // 块按偶数字节对齐
                }

                if (data == null) throw new InvalidDataException("WAV 缺少 data 块");
                if (bits != 16) throw new NotSupportedException($"仅支持 16-bit PCM，实际 {bits}-bit");
                if (channels < 1) channels = 1;

                var pcm = channels == 1 ? data : DownmixToMono(data, channels);
                return new WavData
                {
                    Pcm16 = pcm,
                    SampleRate = sampleRate,
                    Channels = channels,
                    BitsPerSample = bits,
                };
            }
        }

        /// <summary>只取裸 PCM16（去 RIFF 头），wav 为单声道时直接返回 data 段。</summary>
        public static byte[] ReadPcm16(string path) => Read(path).Pcm16;

        private static byte[] DownmixToMono(byte[] interleaved, int channels)
        {
            int frames = interleaved.Length / (2 * channels);
            var mono = new byte[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int idx = (f * channels + c) * 2;
                    sum += BinaryPrimitives.ReadInt16LittleEndian(interleaved.AsSpan(idx, 2));
                }
                short avg = (short)(sum / channels);
                BinaryPrimitives.WriteInt16LittleEndian(mono.AsSpan(f * 2, 2), avg);
            }
            return mono;
        }

        private static bool IsAscii(byte[] bytes, string expected)
        {
            if (bytes == null || bytes.Length < expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (bytes[i] != (byte)expected[i]) return false;
            }
            return true;
        }
    }

    /// <summary>音频重采样/格式转换（16-bit 单声道 PCM）。</summary>
    public static class AudioUtil
    {
        /// <summary>
        /// 重采样（16-bit 单声道 in → out）。
        ///
        /// **降采样必须先抗混叠**：44.1k→24k 这种**非整数倍率**下，早先的「逐输出点取窗口平均」
        /// 会让每个输出点跨的输入样本数在 1/2/3 之间跳变 —— 等于一个**逐点变化**的平滑器，
        /// 结果是周期性包络起伏（实测听感就是「声音一段一段的、发闷」）。现在改成：
        /// ① 先用窗函数 sinc **低通**到目标奈奎斯特（去掉会折叠回来的高频）；
        /// ② 再在滤波后的信号上做线性插值抽点。滤波器固定，包络就平了。
        /// 升采样只需插值（不会混叠），跳过低通。
        /// </summary>
        public static byte[] Resample16(byte[] pcm16, int fromRate, int toRate)
        {
            if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
            if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate));
            if (fromRate == toRate || pcm16.Length == 0) return pcm16;

            int inFrames = pcm16.Length / 2;
            var src = new float[inFrames];
            for (int i = 0; i < inFrames; i++)
            {
                src[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2)) / 32768f;
            }

            if (toRate < fromRate) src = LowPass(src, fromRate, toRate);

            double step = (double)fromRate / toRate;
            int outFrames = (int)Math.Round(inFrames * ((double)toRate / fromRate));
            var outBytes = new byte[outFrames * 2];

            for (int i = 0; i < outFrames; i++)
            {
                double pos = i * step;
                int i0 = (int)pos;
                if (i0 > inFrames - 1) i0 = inFrames - 1;
                int i1 = Math.Min(i0 + 1, inFrames - 1);
                float frac = (float)(pos - i0);

                float v = src[i0] + (src[i1] - src[i0]) * frac;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                BinaryPrimitives.WriteInt16LittleEndian(outBytes.AsSpan(i * 2, 2), (short)Math.Round(v * 32767f));
            }

            return outBytes;
        }

        /// <summary>
        /// 窗函数 sinc 低通（Hann 窗，33 抽头，截止 = 目标奈奎斯特 × 0.94）。
        /// **固定系数**是关键：滤波器不随输出点变化，降采样后不会有周期性包络起伏。
        /// 边界用夹取（clamp）取样，避免首尾出现爆音。
        /// </summary>
        static float[] LowPass(float[] src, int fromRate, int toRate)
        {
            const int taps = 33;
            const int center = taps / 2;
            double fc = (0.5 * toRate * 0.94) / fromRate;   // 归一化截止（周期/采样）

            var h = new double[taps];
            double sum = 0;
            for (int n = 0; n < taps; n++)
            {
                double t = n - center;
                double sinc = Math.Abs(t) < 1e-9 ? 1.0 : Math.Sin(2 * Math.PI * fc * t) / (2 * Math.PI * fc * t);
                double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (taps - 1));   // Hann
                h[n] = sinc * window;
                sum += h[n];
            }
            for (int n = 0; n < taps; n++) h[n] /= sum;        // 归一化：直流增益 = 1

            var y = new float[src.Length];
            for (int k = 0; k < src.Length; k++)
            {
                double acc = 0;
                for (int n = 0; n < taps; n++)
                {
                    int idx = k + center - n;
                    if (idx < 0) idx = 0;
                    else if (idx > src.Length - 1) idx = src.Length - 1;
                    acc += h[n] * src[idx];
                }
                y[k] = (float)acc;
            }
            return y;
        }

        /// <summary>
        /// 峰值归一化：把 PCM16 的峰值拉到 <paramref name="targetPeak"/>（满量程的比例，默认 0.9）。
        ///
        /// 动机（实测反馈「声音还很小」）：melo-tts 原生输出的峰值只有满量程的 0.3–0.5，
        /// 语音库与实时合成都偏轻。放在 Core 里是为了**语音库和自由句走同一条路**，
        /// 响度一致；同时可单测。
        /// </summary>
        public static byte[] NormalizePeak16(byte[] pcm16, float targetPeak = 0.9f)
        {
            if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
            if (pcm16.Length < 2) return pcm16;

            int frames = pcm16.Length / 2;
            int peak = 0;
            for (int i = 0; i < frames; i++)
            {
                int s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
                int mag = s < 0 ? -s : s;
                if (mag > peak) peak = mag;
            }
            if (peak == 0) return pcm16;   // 全静音：没有可放大的东西

            double target = Math.Clamp(targetPeak, 0.05f, 1f) * 32767.0;
            double gain = target / peak;
            if (Math.Abs(gain - 1.0) < 0.01) return pcm16;   // 已经够响，不做无意义的搬动

            var outBytes = new byte[pcm16.Length];
            for (int i = 0; i < frames; i++)
            {
                int s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
                double v = s * gain;
                if (v > 32767) v = 32767;
                else if (v < -32767) v = -32767;
                BinaryPrimitives.WriteInt16LittleEndian(outBytes.AsSpan(i * 2, 2), (short)Math.Round(v));
            }
            return outBytes;
        }

        /// <summary>16kHz 抽取（48k→16k 等整数倍降采样）。</summary>
        public static byte[] Decimate16(byte[] pcm16, int factor)
        {
            if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
            if (factor <= 1) return pcm16;
            int inFrames = pcm16.Length / 2;
            int outFrames = inFrames / factor;
            var outBytes = new byte[outFrames * 2];
            for (int i = 0; i < outFrames; i++)
            {
                int idx = i * factor * 2;
                outBytes[i * 2] = pcm16[idx];
                outBytes[i * 2 + 1] = pcm16[idx + 1];
            }
            return outBytes;
        }

        /// <summary>
        /// float 采样（[-1,1]）→ 16-bit 小端 PCM。Unity 的 <c>Microphone</c> 给的是 float[]，
        /// 上行必须是 PCM16，转换放在共享库里以便单测（越界与非数一律夹到合法值）。
        /// </summary>
        public static byte[] ToPcm16(float[] samples)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            var outBytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i];
                if (float.IsNaN(v)) v = 0f;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                BinaryPrimitives.WriteInt16LittleEndian(outBytes.AsSpan(i * 2, 2), (short)Math.Round(v * 32767f));
            }
            return outBytes;
        }

        /// <summary>16-bit 小端 PCM → float 采样（[-1,1]）。下行 TTS 音频交给 Unity <c>AudioClip</c> 用。</summary>
        public static float[] ToFloat(byte[] pcm16)
        {
            if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
            int frames = pcm16.Length / 2;
            var samples = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2)) / 32768f;
            }
            return samples;
        }
    }
}