using System.Buffers.Binary;
using Copilot.Core;
using SherpaOnnx;

namespace Copilot.AIService.Asr;

/// <summary>
/// Silero VAD（sherpa-onnx ONNX，IT方案1.md V2.0 §5.1）。
/// 语义：喂音频 → 检测到「一段语音 + 其后足够静音」时内部产出一个 segment，
/// 此时 <see cref="SilenceExceeded"/> 为 true，该收尾本句了。
/// </summary>
public sealed class SileroVad : IVad, IDisposable
{
    private readonly VoiceActivityDetector _vad;

    public string Name => "silero";
    public bool IsAvailable => true;

    private SileroVad(VoiceActivityDetector vad) => _vad = vad;

    public static SileroVad? TryCreate(Config.VadOptions options, string modelPath, out string reason)
    {
        if (!File.Exists(modelPath))
        {
            reason = $"VAD 模型不存在：{modelPath}";
            return null;
        }

        try
        {
            var config = new VadModelConfig();
            config.SileroVad.Model = modelPath;
            config.SileroVad.Threshold = options.Threshold;
            config.SileroVad.MinSilenceDuration = options.MinSilenceDuration;
            config.SileroVad.MinSpeechDuration = options.MinSpeechDuration;
            config.SileroVad.WindowSize = 512;
            config.SileroVad.MaxSpeechDuration = 20f;
            config.SampleRate = 16000;
            config.NumThreads = 1;
            config.Provider = "cpu";

            reason = string.Empty;
            return new SileroVad(new VoiceActivityDetector(config, 30f));
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    public bool Accept(byte[] pcm16)
    {
        if (pcm16 == null || pcm16.Length < 2) return false;
        _vad.AcceptWaveform(ToFloat(pcm16));
        return _vad.IsSpeechDetected();
    }

    /// <summary>内部已产出一个「语音段」= 检测到说完。</summary>
    public bool SilenceExceeded => !_vad.IsEmpty();

    public void Reset() => _vad.Reset();

    internal static float[] ToFloat(byte[] pcm16)
    {
        int count = pcm16.Length / 2;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2)) / 32768f;
        }
        return samples;
    }

    public void Dispose() => _vad.Dispose();
}

/// <summary>VAD 工厂：auto 时 silero → energy（能量阈值兜底）。</summary>
public static class VadFactory
{
    public static IVad? Create(Config config, out string detail)
    {
        var backend = (config.Vad.Backend ?? "auto").Trim().ToLowerInvariant();
        var energy = new EnergyVad(16000, 400f, config.Vad.MinSilenceDuration);

        if (backend == "none")
        {
            detail = "已禁用 VAD";
            return null;
        }

        if (backend == "energy")
        {
            detail = "energy（能量阈值）";
            return energy;
        }

        var modelPath = config.ModelFile(config.Vad.ModelPath);
        var silero = SileroVad.TryCreate(config.Vad, modelPath, out var reason);
        if (silero != null)
        {
            detail = $"silero（{Path.GetFileName(modelPath)}，阈值 {config.Vad.Threshold}，静音 {config.Vad.MinSilenceDuration}s）";
            return silero;
        }

        if (backend == "silero")
        {
            throw new InvalidOperationException($"指定 silero 后端但不可用：{reason}");
        }

        detail = $"降级 energy（{reason}）";
        return energy;
    }
}