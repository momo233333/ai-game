using System.Buffers.Binary;
using Copilot.Core;
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
    private readonly int _targetSampleRate;

    public string Name => "sherpa-vits";
    public bool IsAvailable => true;

    /// <summary>
    /// 对外交付的采样率 = `tts.sampleRate`（默认 24000），**不是模型的原生采样率**。
    /// melo-tts 原生 44100Hz，直接下发会让「语音库(24k) vs 自由句(44.1k)」两套码流不一致、
    /// gRPC 载荷也白白翻 1.84 倍；统一重采样到 24kHz（全项目约定：`voice-corpus` 与 manifest 都是 24k）。
    /// </summary>
    public int SampleRate => _targetSampleRate;

    private SherpaVitsRealtimeTts(OfflineTts tts, int targetSampleRate)
    {
        _tts = tts;
        _targetSampleRate = targetSampleRate;
    }

    public static SherpaVitsRealtimeTts? TryCreate(Config.TtsOptions.VitsOptions options, string modelsDir,
        int targetSampleRate, out string reason)
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

            // ⚠️ data_dir 只在 piper（espeak-ng-data）类模型上才填：sherpa-onnx 一旦看到它非空，
            // 就会去校验 <data_dir>/phontab 是否存在，melo-tts 因此直接建不出 TTS（报 "Errors in config"）。
            var dataDir = ResolveOptional(modelsDir, options.DataDir);
            if (dataDir != null) config.Model.Vits.DataDir = dataDir;

            // melo-tts 这类「带中文分词」的模型需要 jieba 词典目录 + 数字/日期规则 FST，缺了会说错或直接不出声
            var dictDir = ResolveOptional(modelsDir, options.DictDir);
            if (dictDir != null) config.Model.Vits.DictDir = dictDir;
            var ruleFsts = ResolveRuleFsts(modelsDir, options.RuleFsts);
            if (ruleFsts != null) config.RuleFsts = ruleFsts;

            if (options.LengthScale > 0) config.Model.Vits.LengthScale = options.LengthScale;

            config.Model.NumThreads = options.NumThreads;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;

            reason = string.Empty;
            return new SherpaVitsRealtimeTts(new OfflineTts(config), targetSampleRate);
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

        try
        {
            var audio = _tts.Generate(text, 1.0f, 0);
            try
            {
                var pcm = ToPcm16(audio.Samples);
                int native = _tts.SampleRate;
                var outPcm = native == _targetSampleRate || native <= 0
                    ? pcm
                    : AudioUtil.Resample16(pcm, native, _targetSampleRate);
                return AppendTrailingSilence(outPcm, _targetSampleRate);
            }
            finally
            {
                audio.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 原生层配置无效时 SherpaOnnxCreateOfflineTts 只打印错误并返回空句柄（不抛异常），
            // 之后每次 Generate 都会炸在这里 —— 必须挡住，否则整个伴生进程直接崩。
            Console.Error.WriteLine($"[vits] 合成失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 给生成音频补一段**尾静音**。实测 melo-tts 的最后一个采样点就是话音（末 50ms RMS≈400–600，
    /// 而 SAPI 生成的是 0），不补的话：(a) 游戏里听起来最后两个字被掐断；
    /// (b) 流式 ASR 收敛不了尾部 —— 实测「我手心的汗把方向盘都弄湿了」只识别出前半句。
    /// </summary>
    private static byte[] AppendTrailingSilence(byte[] pcm16, int sampleRate, double seconds = 0.3)
    {
        int padBytes = (int)(sampleRate * seconds) * 2;
        if (padBytes <= 0) return pcm16;
        var padded = new byte[pcm16.Length + padBytes]; // 新增部分默认全 0 = 静音
        Buffer.BlockCopy(pcm16, 0, padded, 0, pcm16.Length);
        return padded;
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

    /// <summary>可选路径：空配置 / 不存在都返回 null（不阻断，缺了只是少一层规则处理）。</summary>
    private static string? ResolveOptional(string modelsDir, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var p = Resolve(modelsDir, relative!);
        return File.Exists(p) || Directory.Exists(p) ? p : null;
    }

    /// <summary>规则 FST 列表（逗号分隔）→ sherpa-onnx 要求的 <c>a.fst,b.fst</c> 形式；全都不存在时返回 null。</summary>
    private static string? ResolveRuleFsts(string modelsDir, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var parts = relative!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolved = new List<string>();
        foreach (var part in parts)
        {
            var p = ResolveOptional(modelsDir, part);
            if (p != null) resolved.Add(p);
        }
        return resolved.Count > 0 ? string.Join(",", resolved) : null;
    }

    public void Dispose() => _tts.Dispose();
}

/// <summary>
/// TTS 实时后端工厂（语音库始终可用，实时合成按需）。
/// `auto` 的优先级：**vits（模型在）→ sapi（Windows SAPI）→ 无（只剩语音库）**，
/// 与 VAD 的 `silero → energy` 同一降级约定。两条实时链路都不在时，路由停在 ③ 按意图兜底。
/// </summary>
public static class RealtimeTtsFactory
{
    public static IRealtimeTts? Create(Config config, out string detail)
    {
        var backend = (config.Tts.Backend ?? "auto").Trim().ToLowerInvariant();
        if (backend == "voicelib" || backend == "none")
        {
            detail = "仅语音库（实时合成已禁用）";
            return null;
        }

        var modelsDir = config.ResolvePath(config.ModelsDir);
        var engine = SherpaVitsRealtimeTts.TryCreate(config.Tts.Vits, modelsDir, config.Tts.SampleRate, out var vitsReason);
        if (engine != null)
        {
            detail = $"sherpa-vits（实时合成已就绪，输出 {engine.SampleRate}Hz）";
            return engine;
        }

        if (backend == "vits")
        {
            throw new InvalidOperationException($"指定 vits 后端但不可用：{vitsReason}");
        }

        var sapi = WindowsSapiTts.TryCreate(config.Tts.Sapi, out var sapiReason);
        if (sapi != null)
        {
            detail = $"sapi（{sapi.VoiceName}）—— 实时合成已就绪；{vitsReason}";
            return sapi;
        }

        if (backend == "sapi")
        {
            throw new InvalidOperationException($"指定 sapi 后端但不可用：{sapiReason}");
        }

        detail = $"仅语音库（{vitsReason}；SAPI 也不可用：{sapiReason}）";
        return null;
    }
}