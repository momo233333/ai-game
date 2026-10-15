using System;
using System.Buffers.Binary;
using Copilot.Core;
using Xunit;

namespace Copilot.Tests;

public class VoiceLibTests
{
    [Fact]
    public void 加载仓库语音库_条数与音频齐全()
    {
        var lib = TestPaths.LoadVoiceLib();

        // 条数用「不少于」而不是精确等于：语料只会增加（P 轮加 71 条、W 轮加 6 条玩法台词），
        // 每次加台词都要回来改测试数字是纯负担；这里要防的是「语料被删」而不是「被加」。
        Assert.True(lib.Items.Count >= 71, $"语音库条数不足：{lib.Items.Count}");
        Assert.Equal(24000, lib.SampleRate);
        Assert.All(lib.Items, e => Assert.NotNull(e.AbsoluteWavPath));
    }

    [Fact]
    public void 按id查词条()
    {
        var entry = TestPaths.LoadVoiceLib().FindById("L001");
        Assert.NotNull(entry);
        Assert.Equal("好，左转……我看着点后视镜", entry!.Text);
    }

    [Fact]
    public void 按文本精确匹配()
    {
        var entry = TestPaths.LoadVoiceLib().FindByText("好，左转……我看着点后视镜");
        Assert.Equal("L001", entry?.Id);
    }

    [Fact]
    public void 按意图加情绪选到最高优先级()
    {
        var entry = TestPaths.LoadVoiceLib().FindByIntent(Intents.TurnLeft, Emotion.Calm);
        Assert.NotNull(entry);
        Assert.Contains("TURN_LEFT", entry!.IntentTags);
        Assert.Equal("CALM", entry.Emotion);
    }

    [Fact]
    public void 情绪不匹配时退化为只看意图()
    {
        var entry = TestPaths.LoadVoiceLib().FindByIntent(Intents.Park, Emotion.Happy);
        Assert.NotNull(entry);
        Assert.Contains("PARK", entry!.IntentTags);
    }

    [Fact]
    public void PCM取出的是裸数据_不含RIFF头()
    {
        var lib = TestPaths.LoadVoiceLib();
        var entry = lib.FindById("L001")!;

        var pcm = lib.Pcm16(entry);
        var whole = File.ReadAllBytes(entry.AbsoluteWavPath!);

        Assert.NotNull(pcm);
        Assert.Equal(whole.Length - 44, pcm!.Length);
        Assert.NotEqual((byte)'R', pcm[0]);
    }

    [Fact]
    public void 路径缺失时返回空库而不是抛异常()
    {
        var lib = VoiceLib.Load(Path.Combine(TestPaths.RepoRoot, "voice-corpus", "not-exist.json"));
        Assert.Empty(lib.Items);
        Assert.Null(lib.FindById("L001"));
    }
}

public class WavReaderTests
{
    [Fact]
    public void 解析语音库wav_得到24k单声道16bit()
    {
        var wav = WavReader.Read(TestPaths.SampleWav("L001"));

        Assert.Equal(24000, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        Assert.Equal(16, wav.BitsPerSample);
        Assert.True(wav.Pcm16.Length > 2000);
    }

    [Fact]
    public void 非wav文件抛InvalidData()
    {
        var path = Path.Combine(Path.GetTempPath(), "copilot_not_wav.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        try
        {
            Assert.ThrowsAny<Exception>(() => WavReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 重采样改变帧数且保持16bit对齐()
    {
        var wav = WavReader.Read(TestPaths.SampleWav("L001"));
        var resampled = AudioUtil.Resample16(wav.Pcm16, 24000, 16000);

        int expected = wav.Pcm16.Length / 2 * 16000 / 24000;
        Assert.InRange(resampled.Length, expected * 2 - 2, expected * 2 + 2);
    }

    [Fact]
    public void 采样率相同时原样返回()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        Assert.Same(pcm, AudioUtil.Resample16(pcm, 16000, 16000));
    }

    [Fact]
    public void 整数倍抽取_48k到16k()
    {
        var pcm = new byte[48];
        var outPcm = AudioUtil.Decimate16(pcm, 3);
        Assert.Equal(16, outPcm.Length);
    }

    [Fact]
    public void float转PCM16_越界与非数被夹到合法值()
    {
        var pcm = AudioUtil.ToPcm16(new[] { 0f, 1f, -1f, 2.5f, -3f, float.NaN });

        Assert.Equal(12, pcm.Length);
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0, 2)));
        Assert.Equal(32767, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2, 2)));
        Assert.Equal(-32767, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(4, 2)));
        Assert.Equal(32767, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(6, 2)));
        Assert.Equal(-32767, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(8, 2)));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(10, 2)));
    }

    [Fact]
    public void PCM16与float互转_往返一致()
    {
        var original = new[] { 0f, 0.5f, -0.5f, 1f, -1f };

        var roundTrip = AudioUtil.ToFloat(AudioUtil.ToPcm16(original));

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i], roundTrip[i], 3);
        }
    }

    // ---- 44.1k→24k 这条非整数倍率重采样（melo-tts 的原生输出，实测「一段一段」投诉的正主）----

    static byte[] Sine(float freq, float seconds, int rate, float amplitude = 0.5f)
    {
        int frames = (int)(rate * seconds);
        var pcm = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            short s = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * freq * i / rate) * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), s);
        }
        return pcm;
    }

    static double Rms(byte[] pcm, int fromFrame, int toFrame)
    {
        double sum = 0; int n = 0;
        for (int i = fromFrame; i < toFrame; i++)
        {
            double v = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768.0;
            sum += v * v; n++;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }

    [Fact]
    public void 降采样后正弦幅度平稳_没有周期性起伏()
    {
        // 回归（实测投诉「声音一段一段的」）：44.1k→24k 是非整数倍率 147/80，
        // 早先「按输出点取输入窗口平均」的实现会让窗口在 2/3 样本间跳变 → 逐点变化的平滑器 → 包络周期性起伏。
        // 6kHz 仍在 12kHz 奈奎斯特以内，降采样后幅度必须基本不变（旧实现下起伏约 18%）。
        var src = Sine(6000f, 0.5f, 44100, 0.5f);
        var dst = AudioUtil.Resample16(src, 44100, 24000);

        int block = 24000 / 200;   // 5ms
        int frames = dst.Length / 2;
        double min = double.MaxValue, max = 0;
        for (int start = 1200; start + block <= frames - 1200; start += block)   // 跳过首尾
        {
            double r = Rms(dst, start, start + block);
            if (r < min) min = r;
            if (r > max) max = r;
        }

        Assert.True(max / Math.Max(min, 1e-9) < 1.12,
            $"降采样后出现周期性起伏：min={min:0.0000} max={max:0.0000}");
    }

    [Fact]
    public void 降采样滤掉高于目标奈奎斯特的频率()
    {
        // 15kHz 在 24kHz 的奈奎斯特（12kHz）之外，抗混叠低通必须把它压掉，
        // 否则会折叠成 9kHz 的噪声（听感是"沙沙"的金属音）。
        var high = AudioUtil.Resample16(Sine(15000f, 0.5f, 44100, 0.5f), 44100, 24000);
        var low = AudioUtil.Resample16(Sine(1000f, 0.5f, 44100, 0.5f), 44100, 24000);

        double rmsHigh = Rms(high, 2000, high.Length / 2 - 2000);
        double rmsLow = Rms(low, 2000, low.Length / 2 - 2000);

        Assert.True(rmsHigh < rmsLow * 0.2,
            $"15kHz 没有被压掉：high={rmsHigh:0.0000} low={rmsLow:0.0000}");
    }

    [Fact]
    public void 峰值归一化_把偏轻的音频拉到目标峰值()
    {
        // melo-tts 原生输出峰值只有满量程的 0.3–0.5 → 实测「声音很小」
        var quiet = Sine(440f, 0.2f, 24000, 0.35f);
        var loud = AudioUtil.NormalizePeak16(quiet, 0.9f);

        int peak = 0;
        for (int i = 0; i < loud.Length / 2; i++)
        {
            int s = BinaryPrimitives.ReadInt16LittleEndian(loud.AsSpan(i * 2, 2));
            if (Math.Abs(s) > peak) peak = Math.Abs(s);
        }

        Assert.InRange(peak, (int)(0.9 * 32767 * 0.97), (int)(0.9 * 32767 * 1.01));
    }

    [Fact]
    public void 峰值归一化_全静音原样返回()
    {
        var silence = new byte[200];
        Assert.Same(silence, AudioUtil.NormalizePeak16(silence));
    }

    [Fact]
    public void 峰值归一化_不会越界溢出()
    {
        var loud = Sine(440f, 0.1f, 24000, 0.95f);
        var normalized = AudioUtil.NormalizePeak16(loud, 0.9f);

        for (int i = 0; i < normalized.Length / 2; i++)
        {
            int s = BinaryPrimitives.ReadInt16LittleEndian(normalized.AsSpan(i * 2, 2));
            Assert.InRange(s, short.MinValue, short.MaxValue);
        }
    }
}