using System.Buffers.Binary;
using Copilot.Core.Tts;
using SherpaOnnx;

namespace Copilot.AIService.Tts;

/// <summary>
/// TTS 路由 ②：sherpa-onnx VITS 实时合成（IT方案1.md V2.0 §5.5）。
/// 只用于未被语音库命中的自由句；模型缺失时该后端不可用，路由自动停在语音库。
/// </summary>
public sealed class SherpaVitsRealtimeTts : IRealtimeTts, IDisposable
{
    private readonly OfflineTts _tts;

    public string Name => "sherpa-vits";
    public bool IsAvailable => true;
    public int SampleRate => _tts.SampleRate;

    private SherpaVitsRealtimeTts(OfflineTts tts) => _tts = tts;

    public static SherpaVitsRealtimeTts? TryCreate(Config.TtsOptions.VitsOptions options, string modelsDir, out string reason)
    {
        string model = Resolve(modelsDir, options.Model);
        string tokens = Resolve(modelsDir, options.Tokens);
        string lexicon = Resolve(modelsDir, options.Lexicon);

        if (!File.Exists(model) || !File.Exists(tokens))
        {
            reason = $"VITS 模型缺失（需 model.onnx + tokens.txt）：{modelsDir}";
            return null;
        }

        try
        {
            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = model;
            config.Model.Vits.Tokens = tokens;
            if (File.Exists(lexicon)) config.Model.Vits.Lexicon = lexicon;
            config.Model.Vits.DataDir = Path.GetDirectoryName(model) ?? modelsDir;
            config.Model.NumThreads = options.NumThreads;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;

            reason = string.Empty;
            return new SherpaVitsRealtimeTts(new OfflineTts(config));
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    public byte[]? Synthesize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var audio = _tts.Generate(text, 1.0f, 0);
        try
        {
            return ToPcm16(audio.Samples);
        }
        finally
        {
            audio.Dispose();
        }
    }

    internal static byte[] ToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            short s = (short)(v * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), s);
        }
        return bytes;
    }

    private static string Resolve(string modelsDir, string relative)
        => Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(modelsDir, relative));

    public void Dispose() => _tts.Dispose();
}

/// <summary>TTS 实时后端工厂（语音库始终可用，实时合成按需）。</summary>
public static class RealtimeTtsFactory
{
    public static IRealtimeTts? Create(Config config, out string detail)
    {
        var backend = (config.Tts.Backend ?? "auto").Trim().ToLowerInvariant();
        if (backend == "voicelib")
        {
            detail = "仅语音库（实时合成已禁用）";
            return null;
        }

        var modelsDir = config.ResolvePath(config.ModelsDir);
        var engine = SherpaVitsRealtimeTts.TryCreate(config.Tts.Vits, modelsDir, out var reason);
        if (engine != null)
        {
            detail = "sherpa-vits（实时合成已就绪）";
            return engine;
        }

        if (backend == "vits")
        {
            throw new InvalidOperationException($"指定 vits 后端但不可用：{reason}");
        }

        detail = $"仅语音库（{reason}）";
        return null;
    }
}