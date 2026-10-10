using System.Reflection;
using Copilot.Core;
using Copilot.Core.Dialogue;
using Copilot.Core.Llm;
using Copilot.Core.Tts;
using Copilot.AIService.Asr;
using Copilot.AIService.Llm;
using Copilot.AIService.Tts;

namespace Copilot.AIService;

/// <summary>
/// AI 伴生进程运行时：按 config 装配 LLM / ASR / TTS / 语音库 / 安全层，
/// 并持有唯一的 <see cref="TurnPipeline"/> 与当前 GameState（供 SyncGameState 更新）。
/// 所有后端都走「首选 → 降级」，任一环节缺失都不影响进程启动（IT方案1.md V2.0 §1.1）。
/// </summary>
public sealed class AiRuntime : IDisposable
{
    public Config Config { get; }
    public ILlmBackend Llm { get; }
    public IAsrBackend? Asr { get; }
    public IRealtimeTts? RealtimeTts { get; }

    /// <summary>断句用的 VAD（麦克风连续聆听时用；无模型时降级为能量阈值）。</summary>
    public IVad? Vad { get; }
    public VoiceLib VoiceLib { get; }
    public TtsRouter Tts { get; }
    public SafetyFilter Safety { get; }
    public TurnPipeline Pipeline { get; }

    /// <summary>最近一次 SyncGameState 的完整状态（文本回合若未带状态则用它）。</summary>
    public GameStateSnapshot CurrentState { get; private set; } = new();

    public string LlmDetail { get; }
    public string AsrDetail { get; }
    public string TtsDetail { get; }
    public string VadDetail { get; }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private AiRuntime(Config config, ILlmBackend llm, IAsrBackend? asr, IRealtimeTts? realtime,
        IVad? vad, VoiceLib voiceLib, TtsRouter tts, SafetyFilter safety, TurnPipeline pipeline,
        string llmDetail, string asrDetail, string ttsDetail, string vadDetail)
    {
        Config = config;
        Llm = llm;
        Asr = asr;
        RealtimeTts = realtime;
        Vad = vad;
        VoiceLib = voiceLib;
        Tts = tts;
        Safety = safety;
        Pipeline = pipeline;
        LlmDetail = llmDetail;
        AsrDetail = asrDetail;
        TtsDetail = ttsDetail;
        VadDetail = vadDetail;
    }

    public static AiRuntime Create(Config config, Action<string>? log = null)
    {
        var llm = LlmBackendFactory.Create(config, out var llmDetail);
        log?.Invoke($"[llm] {llm.Name} — {llmDetail}");

        var asr = AsrBackendFactory.Create(config, out var asrDetail);
        log?.Invoke($"[asr] {asr?.Name ?? "none"} — {asrDetail}");

        var vad = VadFactory.Create(config, out var vadDetail);
        log?.Invoke($"[vad] {vad?.Name ?? "none"} — {vadDetail}");

        var realtime = RealtimeTtsFactory.Create(config, out var ttsDetail);
        log?.Invoke($"[tts] {realtime?.Name ?? "none"} — {ttsDetail}");

        var manifest = config.ResolvePath(config.Tts.VoicelibManifest);
        var voiceLib = VoiceLib.Load(manifest);
        var ready = voiceLib.Items.Count(e => e.AbsoluteWavPath != null);
        log?.Invoke($"[voicelib] {ready}/{voiceLib.Items.Count} 条音频就绪（{manifest}）");

        var tts = new TtsRouter(voiceLib, realtime);

        var wordsFile = config.ResolvePath(config.Safety.WordsFile);
        var safety = SafetyFilter.FromFile(wordsFile);
        log?.Invoke($"[safety] {safety.Count} 个敏感词（{wordsFile}）");

        var trust = new TrustState();
        var memory = new DialogueMemory();
        var pipeline = new TurnPipeline(llm, tts, safety, trust, memory);

        return new AiRuntime(config, llm, asr, realtime, vad, voiceLib, tts, safety, pipeline,
            llmDetail, asrDetail, ttsDetail, vadDetail);
    }

    public void UpdateState(GameStateSnapshot state)
    {
        if (state == null) return;
        CurrentState = state;
        Pipeline.Trust.Set(state.Trust, state.Emotion);
    }

    public void Dispose()
    {
        (Llm as IDisposable)?.Dispose();
        (Asr as IDisposable)?.Dispose();
        (RealtimeTts as IDisposable)?.Dispose();
        (Vad as IDisposable)?.Dispose();
    }
}