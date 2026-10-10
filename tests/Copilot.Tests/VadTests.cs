using System.Buffers.Binary;
using Copilot.Core;
using Xunit;

namespace Copilot.Tests;

/// <summary>
/// 能量阈值 VAD 的纯逻辑测试（Silero 需要模型，归到真机验证；这里保证兜底路径与
/// 「说话 → 停顿 → 收尾」的判句语义正确）。
/// </summary>
public class EnergyVadTests
{
    private const int Rate = 16000;

    private static byte[] Silence(int ms)
        => new byte[Rate * ms / 1000 * 2];

    private static byte[] Loud(int ms, short amplitude = 8000)
    {
        int frames = Rate * ms / 1000;
        var bytes = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), (short)(i % 2 == 0 ? amplitude : -amplitude));
        }
        return bytes;
    }

    [Fact]
    public void 静音不判为语音()
    {
        var vad = new EnergyVad(Rate);

        Assert.False(vad.Accept(Silence(50)));
        Assert.False(vad.SilenceExceeded);
    }

    [Fact]
    public void 大声帧判为语音()
    {
        var vad = new EnergyVad(Rate);
        Assert.True(vad.Accept(Loud(50)));
    }

    [Fact]
    public void 说话前长静音不会误判说完()
    {
        var vad = new EnergyVad(Rate);

        for (int i = 0; i < 40; i++) vad.Accept(Silence(50)); // 2s 静音

        Assert.False(vad.SilenceExceeded);
    }

    [Fact]
    public void 说了一句话后停顿超阈值判定说完()
    {
        var vad = new EnergyVad(Rate, minSilenceSeconds: 0.6);

        Assert.True(vad.Accept(Loud(300)));
        Assert.False(vad.SilenceExceeded);

        // 静音累计到 0.6s（12 帧 × 50ms）时才收尾
        for (int i = 0; i < 11; i++)
        {
            vad.Accept(Silence(50));
            Assert.False(vad.SilenceExceeded);
        }

        vad.Accept(Silence(50));
        Assert.True(vad.SilenceExceeded);
    }

    [Fact]
    public void Reset后回到未说话状态()
    {
        var vad = new EnergyVad(Rate);
        vad.Accept(Loud(200));
        for (int i = 0; i < 20; i++) vad.Accept(Silence(50));
        Assert.True(vad.SilenceExceeded);

        vad.Reset();

        Assert.False(vad.SilenceExceeded);
        Assert.False(vad.Accept(Silence(50)));
    }

    [Fact]
    public void 说话中途的短暂停顿被重置_不误判说完()
    {
        var vad = new EnergyVad(Rate, minSilenceSeconds: 0.6);

        vad.Accept(Loud(100));
        vad.Accept(Silence(50));  // 短暂停顿
        Assert.False(vad.SilenceExceeded);

        vad.Accept(Loud(100));    // 又说了 → 静音计数归零
        vad.Accept(Silence(50));

        Assert.False(vad.SilenceExceeded);
    }

    [Fact]
    public void 低于阈值的底噪不算语音()
    {
        var vad = new EnergyVad(Rate, thresholdRms: 400f);
        Assert.False(vad.Accept(Loud(50, amplitude: 200)));
    }

    [Fact]
    public void RMS按帧计算()
    {
        Assert.Equal(0f, EnergyVad.Rms(Silence(10)), 3);
        Assert.Equal(8000f, EnergyVad.Rms(Loud(10, 8000)), 1);
    }

    [Fact]
    public void 空输入与短输入安全()
    {
        var vad = new EnergyVad(Rate);

        Assert.False(vad.Accept(Array.Empty<byte>()));
        Assert.False(vad.Accept(new byte[] { 1 }));
        Assert.False(vad.Accept(null!));
    }

    [Fact]
    public void 非法采样率被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnergyVad(0));
    }
}