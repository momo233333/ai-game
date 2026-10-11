using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Copilot.Core.Tts;

namespace Copilot.AIService.Tts;

/// <summary>
/// TTS 路由 ② 的 Windows 实现：**System.Speech（SAPI）实时合成**。
///
/// 为什么不是方案里的 sherpa-onnx VITS：
/// 1. **音色一致性（决定性原因）** —— `voice-corpus/wavs/` 那 71 条语料就是用本机 SAPI 的
///    `Microsoft Huihui Desktop` 合成的（见 `tools/voicelib_synth.py`）。用同一个引擎做实时合成，
///    命中语音库的台词与实时合成的自由句**是同一个嗓门**；换 VITS 会变成「妹妹一句话一个声音」。
/// 2. **可用性** —— VITS 中文模型在本机拿不到：GitHub Releases 被限速到 0.01 MB/s（开发进度.md §3），
///    ModelScope 上也没有可用的 sherpa-onnx VITS 中文镜像。
///
/// VITS 仍是**首选**（`vits` 后端 / 模型就位时 auto 会用它），SAPI 是它的兜底 —— 与 VAD 的
/// `silero → energy` 降级链同一约定。装上 VITS 并重录语料后音色依然统一。
/// </summary>
public sealed class WindowsSapiTts : IRealtimeTts, IDisposable
{
    /// <summary>合成串行化：SAPI 的语音对象不是线程安全的（gRPC 可能并发进来）。</summary>
    private readonly object _gate = new();
    private readonly SpeechSynthesizer _synth;
    private readonly SpeechAudioFormatInfo _format;

    public string Name => "sapi";
    public bool IsAvailable => true;

    /// <summary>下行采样率。真实合成结果的采样率若与它不符，会在 <see cref="Synthesize"/> 里重采样对齐。</summary>
    public int SampleRate { get; }

    /// <summary>实际选中的嗓音名（诊断用）。</summary>
    public string VoiceName { get; }

    private WindowsSapiTts(SpeechSynthesizer synth, int sampleRate, string voiceName)
    {
        _synth = synth;
        SampleRate = sampleRate;
        VoiceName = voiceName;
        _format = new SpeechAudioFormatInfo(sampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
    }

    public static WindowsSapiTts? TryCreate(Config.TtsOptions.SapiOptions options, out string reason)
    {
        try
        {
            var synth = new SpeechSynthesizer();
            var installed = synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Name)
                .ToList();

            if (installed.Count == 0)
            {
                synth.Dispose();
                reason = "本机没有可用的 SAPI 嗓音（控制面板 → 语音 → 文本转语音）";
                return null;
            }

            var wanted = (options.Voice ?? string.Empty).Trim();
            var picked = PickVoice(installed, wanted) ?? installed[0];
            synth.SelectVoice(picked);

            synth.Rate = options.Rate;
            synth.Volume = options.Volume;

            reason = string.Empty;
            return new WindowsSapiTts(synth, options.SampleRate, picked);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 选嗓音：精确名 → 包含匹配（写 "Huihui" 就能选中 "Microsoft Huihui Desktop"）→ 未指定时不动。
    /// **不用 Culture 匹配**：本进程开了 `InvariantGlobalization`，区域性 API 不可靠，按名字挑最稳。
    /// </summary>
    private static string? PickVoice(IReadOnlyList<string> installed, string wanted)
    {
        if (wanted.Length == 0) return null;

        foreach (var name in installed)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return name;
        }
        foreach (var name in installed)
        {
            if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return name;
        }
        return null;
    }

    public byte[]? Synthesize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            lock (_gate)
            {
                using var ms = new MemoryStream();
                // 注意：SetOutputToAudioStream 写的是**裸 PCM**（按 _format），不带 RIFF 头 —— 不能用 WavReader
                _synth.SetOutputToAudioStream(ms, _format);
                try
                {
                    _synth.Speak(text);
                }
                finally
                {
                    _synth.SetOutputToNull();
                }

                if (ms.Length < 2) return null;

                var raw = ms.ToArray();
                // 采样点是 16-bit，奇数长度说明尾部有半个采样，丢掉
                return raw.Length % 2 == 0 ? raw : raw[..^1];
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sapi] 合成失败：{ex.GetType().Name}: {ex.Message}");
            // 合成失败不该拖垮回合：返回 null，TtsRouter 会继续往下降级（语音库按意图兜底 → 仅字幕）
            return null;
        }
    }

    public void Dispose() => _synth.Dispose();
}