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
        /// <summary>线性插值重采样（16-bit mono in → 16-bit mono out）。</summary>
        public static byte[] Resample16(byte[] pcm16, int fromRate, int toRate)
        {
            if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
            if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate));
            if (fromRate == toRate || pcm16.Length == 0) return pcm16;

            int inFrames = pcm16.Length / 2;
            double ratio = (double)toRate / fromRate;
            int outFrames = (int)Math.Round(inFrames * ratio);
            var outBytes = new byte[outFrames * 2];

            for (int i = 0; i < outFrames; i++)
            {
                double src = i / ratio;
                int i0 = (int)Math.Floor(src);
                int i1 = Math.Min(i0 + 1, inFrames - 1);
                double frac = src - i0;
                short s0 = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i0 * 2, 2));
                short s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i1 * 2, 2));
                short v = (short)Math.Round(s0 + (s1 - s0) * frac);
                BinaryPrimitives.WriteInt16LittleEndian(outBytes.AsSpan(i * 2, 2), v);
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