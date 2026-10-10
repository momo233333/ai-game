using Copilot.Core;
using NAudio.Wave;

namespace Copilot.AIService.Audio;

/// <summary>
/// 伴生进程本机音频 IO（IT方案1.md V2.0 §5.1 / §6.3）：
/// 语音交互阶段用于自测（麦克风 → 识别 → 回放妹妹语音），
/// 游戏阶段麦克风改由 Unity 采集，回放仍可用同一 PCM 契约。
/// </summary>
public static class AudioIo
{
    /// <summary>录音：持续采集至 <paramref name="stopRequested"/> 为真，返回目标采样率的 16-bit 单声道 PCM。</summary>
    public static byte[] RecordTo16k(int targetSampleRate, Func<bool> stopRequested,
        int deviceSampleRate = 48000, int deviceNumber = 0)
    {
        using var capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(deviceSampleRate, 16, 1),
            BufferMilliseconds = 50,
        };

        using var buffer = new MemoryStream();
        capture.DataAvailable += (_, e) =>
        {
            lock (buffer) buffer.Write(e.Buffer, 0, e.BytesRecorded);
        };

        capture.StartRecording();
        var started = DateTime.UtcNow;
        while (!stopRequested() && (DateTime.UtcNow - started).TotalSeconds < 30)
        {
            Thread.Sleep(20);
        }
        capture.StopRecording();

        byte[] raw;
        lock (buffer) raw = buffer.ToArray();

        return deviceSampleRate == targetSampleRate
            ? raw
            : AudioUtil.Resample16(raw, deviceSampleRate, targetSampleRate);
    }

    /// <summary>把 16-bit 单声道 PCM 写成 wav（诊断用：听录音判断是采集问题还是识别问题）。</summary>
    public static void SaveWav16(string path, byte[] pcm16, int sampleRate)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        int dataSize = pcm16.Length;
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                       // fmt 块大小
        bw.Write((short)1);                 // PCM
        bw.Write((short)1);                 // 单声道
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);           // 字节率
        bw.Write((short)2);                 // 块对齐
        bw.Write((short)16);                // 位深
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        bw.Write(pcm16);
    }

    /// <summary>列出所有录音设备（下标即 --device 的取值）。</summary>
    public static IReadOnlyList<string> ListInputDevices()
    {
        var list = new List<string>();
        try
        {
            int count = WaveInEvent.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                list.Add($"[{i}] {caps.ProductName}（{caps.Channels} ch）");
            }
        }
        catch (Exception ex)
        {
            list.Add($"(列举失败：{ex.Message})");
        }
        return list;
    }

    /// <summary>读某个录音设备的名字（越界或失败返回 null）。</summary>
    public static string? DeviceName(int deviceNumber)
    {
        try
        {
            return deviceNumber >= 0 && deviceNumber < WaveInEvent.DeviceCount
                ? WaveInEvent.GetCapabilities(deviceNumber).ProductName
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 持续采集：每 ~50ms 回调一帧（已重采样到 <paramref name="targetSampleRate"/> 的 16-bit 单声道 PCM）。
    /// 供 VAD + 流式识别做「边说边出字幕」，与 <see cref="RecordTo16k"/> 的一次性录音互补。
    /// </summary>
    public static IDisposable StartCapture(int targetSampleRate, Action<byte[]> onFrame,
        int deviceSampleRate = 48000, int deviceNumber = 0)
    {
        var capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(deviceSampleRate, 16, 1),
            BufferMilliseconds = 50,
        };

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;
            var raw = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, raw, 0, e.BytesRecorded);

            var pcm = deviceSampleRate == targetSampleRate
                ? raw
                : AudioUtil.Resample16(raw, deviceSampleRate, targetSampleRate);

            try
            {
                onFrame(pcm);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[audio] 帧回调异常：{ex.Message}");
            }
        };

        capture.StartRecording();
        return new CaptureHandle(capture);
    }

    private sealed class CaptureHandle : IDisposable
    {
        private readonly WaveInEvent _capture;

        public CaptureHandle(WaveInEvent capture) => _capture = capture;

        public void Dispose()
        {
            try
            {
                _capture.StopRecording();
            }
            catch (Exception)
            {
                // 设备已拔出等情况忽略
            }
            _capture.Dispose();
        }
    }

    /// <summary>回放 16-bit 单声道 PCM 到默认输出设备。</summary>
    public static void Play(byte[] pcm16, int sampleRate)
    {
        if (pcm16 == null || pcm16.Length == 0) return;

        using var stream = new RawSourceWaveStream(new MemoryStream(pcm16), new WaveFormat(sampleRate, 16, 1));
        using var output = new WaveOutEvent();
        output.Init(stream);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            Thread.Sleep(20);
        }
    }

    /// <summary>回放一个 wav 文件（按文件自身采样率）。</summary>
    public static void PlayWav(string path)
    {
        var wav = WavReader.Read(path);
        Play(wav.Pcm16, wav.SampleRate);
    }

    /// <summary>是否存在可用的输入设备（无麦克风时给出可读的诊断而不是崩溃）。</summary>
    public static bool HasInputDevice()
    {
        try
        {
            return WaveInEvent.DeviceCount > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}