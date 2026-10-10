using Copilot.Core;
using Xunit;

namespace Copilot.Tests;

public class VoiceLibTests
{
    [Fact]
    public void 加载仓库语音库_条数与音频齐全()
    {
        var lib = TestPaths.LoadVoiceLib();

        Assert.Equal(72, lib.Items.Count);
        Assert.Equal(24000, lib.SampleRate);
        Assert.All(lib.Items, e => Assert.NotNull(e.AbsoluteWavPath));
    }

    [Fact]
    public void 按id查词条()
    {
        var entry = TestPaths.LoadVoiceLib().FindById("L001");
        Assert.NotNull(entry);
        Assert.Equal("行，左转交给我", entry!.Text);
    }

    [Fact]
    public void 按文本精确匹配()
    {
        var entry = TestPaths.LoadVoiceLib().FindByText("行，左转交给我");
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
}