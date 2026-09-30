using System;

namespace Copilot.Core
{
    /// <summary>
    /// TTS 下行 PCM16 处理：服务端 tts_audio.pcm16 以 {"__b64__": "..."} 传输（24kHz mono）。
    /// 转成 float 供 Unity AudioClip 使用；采样率/声道信息一并解析。
    /// </summary>
    public static class PcmAudio
    {
        public sealed class Chunk
        {
            public float[] Samples;
            public int SampleRate = 24000;
            public int Channels = 1;
            public bool FromVoiceLib;
            public string Text = "";
        }

        /// <summary>解析 tts_audio 事件为可播放片段；无音频时返回 null。</summary>
        public static Chunk FromEvent(TurnEvent evt)
        {
            if (evt == null || evt.Data == null) return null;
            if (!evt.Data.ContainsKey("pcm16")) return null;

            byte[] bytes = DecodeBytes(evt.Data["pcm16"]);
            if (bytes == null || bytes.Length < 2) return null;

            int sampleCount = bytes.Length / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short value = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                samples[i] = value / 32768f;
            }

            return new Chunk
            {
                Samples = samples,
                SampleRate = SimpleJson.GetInt(evt.Data, "sample_rate", 24000),
                Channels = SimpleJson.GetInt(evt.Data, "channels", 1),
                FromVoiceLib = SimpleJson.GetBool(evt.Data, "from_voicelib"),
                Text = SimpleJson.GetString(evt.Data, "text"),
            };
        }

        public static byte[] DecodeBytes(object value)
        {
            if (value == null) return null;
            if (value is byte[] raw) return raw;

            var wrapper = SimpleJson.AsDict(value);
            if (wrapper != null)
            {
                string b64 = SimpleJson.GetString(wrapper, "__b64__");
                if (string.IsNullOrEmpty(b64)) return null;
                try
                {
                    return Convert.FromBase64String(b64);
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                var buffer = new System.Collections.Generic.List<byte>();
                foreach (object item in list)
                {
                    if (item is double d) buffer.Add((byte)(int)Math.Round(d));
                }
                return buffer.ToArray();
            }
            return null;
        }

        /// <summary>48kHz → 16kHz 抽取（整数倍，Unity 上行重采样用，方案 §4.1）。</summary>
        public static float[] Downsample(float[] input, int sourceRate, int targetRate)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (sourceRate <= 0 || targetRate <= 0) throw new ArgumentOutOfRangeException("invalid rate");
            if (sourceRate == targetRate) return input;

            int outputLength = (int)((long)input.Length * targetRate / sourceRate);
            if (outputLength <= 0) return new float[0];

            var output = new float[outputLength];
            double ratio = (double)sourceRate / targetRate;
            for (int i = 0; i < outputLength; i++)
            {
                int index = (int)(i * ratio);
                if (index >= input.Length) index = input.Length - 1;
                output[i] = input[index];
            }
            return output;
        }

        /// <summary>float [-1,1] → PCM16 LE 字节。</summary>
        public static byte[] ToPcm16(float[] samples)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                float clamped = samples[i] < -1f ? -1f : (samples[i] > 1f ? 1f : samples[i]);
                short value = (short)Math.Round(clamped * short.MaxValue);
                bytes[i * 2] = (byte)(value & 0xff);
                bytes[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            return bytes;
        }
    }
}
