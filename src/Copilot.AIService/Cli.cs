using System.Runtime.InteropServices;
using Copilot.AIService.Audio;
using Copilot.Core;
using Copilot.Core.Llm;

namespace Copilot.AIService;

/// <summary>
/// 语音交互自测 CLI（IT方案1.md V2.0 阶段 A / P4）：
/// 不依赖 Unity 即可跑通「文本/WAV → 识别 → 指令 → 台词 → 状态」闭环。
/// </summary>
public static class Cli
{
    /// <summary>当前会话日志（仅麦克风交互时打开）。失败时静默为 null。</summary>
    private static SessionLog? _log;

    /// <summary>写一条会话日志（未开日志时是 no-op）。</summary>
    public static void Log(string tag, string message) => _log?.Write(tag, message);

    public static void PrintBanner(AiRuntime runtime)
    {
        Console.WriteLine("========================================");
        Console.WriteLine($" 《副驾指令》AI 伴生进程 v{AiRuntime.Version}");
        Console.WriteLine("========================================");
        Console.WriteLine($"  LLM  : {runtime.Llm.Name}");
        Console.WriteLine($"  ASR  : {runtime.Asr?.Name ?? "none（仅文本模式）"}");
        Console.WriteLine($"  VAD  : {runtime.Vad?.Name ?? "none"}");
        Console.WriteLine($"  TTS  : {runtime.Tts.Name}");
        Console.WriteLine($"  配置 : {runtime.Config.LoadedFrom}");
        Console.WriteLine("========================================");
    }

    public static async Task RunTextAsync(AiRuntime runtime, string text)
    {
        await RunTurnAsync(runtime, text);
    }

    /// <summary>
    /// 跑一个回合并把事件**边产出边打印**（流式），同时记录「动作到达」与「全回合结束」两个时刻。
    /// 「动作到达」才是玩家体感的「说完→车动」，方案 §6.5 的 <1.2s 看的是它。
    /// </summary>
    private static async Task<TurnResult> RunTurnAsync(AiRuntime runtime, string text, string? label = null)
    {
        var result = new TurnResult { ActionMs = -1 };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await foreach (var e in runtime.Pipeline.RunTextTurnStreamAsync(text, runtime.CurrentState))
        {
            if (e.Kind == TurnEventKind.DriveAction && result.ActionMs < 0)
            {
                result.ActionMs = watch.ElapsedMilliseconds;
            }

            result.Events.Add(e);
            PrintEvent(e);
        }

        watch.Stop();
        result.TotalMs = watch.ElapsedMilliseconds;

        Console.WriteLine($"  [耗时]{label} 动作 {result.ActionMs} ms · 全回合 {result.TotalMs} ms（不含模型加载）");
        PrintLlmDiagnostics(runtime);
        return result;
    }

    private sealed class TurnResult
    {
        public long ActionMs;
        public long TotalMs;
        public List<TurnEvent> Events { get; } = new();
    }

    public static async Task RunWavAsync(AiRuntime runtime, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[错误] 音频文件不存在：{path}");
            return;
        }

        if (runtime.Asr == null)
        {
            Console.Error.WriteLine("[错误] ASR 未就绪，无法识别音频。请提供 sherpa-onnx 流式模型，或改用 --text。");
            return;
        }

        var wav = WavReader.Read(path);
        var pcm = wav.SampleRate == runtime.Asr.SampleRate
            ? wav.Pcm16
            : AudioUtil.Resample16(wav.Pcm16, wav.SampleRate, runtime.Asr.SampleRate);

        Console.WriteLine($"  [音频] {Path.GetFileName(path)} {wav.SampleRate}Hz {wav.Channels}ch {wav.Pcm16.Length / 2 / (double)wav.SampleRate:0.00}s");

        var asrWatch = System.Diagnostics.Stopwatch.StartNew();
        string text;
        if (runtime.Asr is IStreamingAsr streaming)
        {
            text = StreamTranscribe(streaming, pcm, runtime.Asr.SampleRate);
        }
        else
        {
            text = await runtime.Asr.TranscribeAsync(pcm);
        }
        asrWatch.Stop();

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine("  [ASR ] （未识别到内容）");
            return;
        }

        Console.WriteLine($"  [ASR ] 识别耗时 {asrWatch.ElapsedMilliseconds} ms");
        await RunTurnAsync(runtime, text);
    }

    /// <summary>
    /// 按 100ms 分块喂入流式识别器，**边喂边打印 partial 字幕**（方案 §5.1 实时上屏）。
    /// 同一段代码路径也是麦克风连续聆听的实现基础。
    /// </summary>
    private static string StreamTranscribe(IStreamingAsr streaming, byte[] pcm16, int sampleRate)
    {
        int chunkBytes = Math.Max(2, sampleRate / 10 * 2); // 100ms
        using var session = streaming.CreateSession();

        string last = string.Empty;
        for (int offset = 0; offset < pcm16.Length; offset += chunkBytes)
        {
            int len = Math.Min(chunkBytes, pcm16.Length - offset);
            var chunk = new byte[len];
            Buffer.BlockCopy(pcm16, offset, chunk, 0, len);

            var partial = session.Accept(chunk);
            if (!string.IsNullOrEmpty(partial) && partial != last)
            {
                Console.WriteLine($"  [字幕] {partial}");
                last = partial;
            }
        }

        return session.Finish();
    }

    /// <summary>
    /// VAD 诊断：把一段 wav 按 50ms 喂进 VAD，报告语音帧占比与检出的句数。
    /// 无麦克风时用它验证 VAD 是否真的能检出语音（尤其换模型/调阈值之后）。
    /// </summary>
    public static void RunVadCheck(AiRuntime runtime, string path)
    {
        if (runtime.Vad == null)
        {
            Console.Error.WriteLine("[错误] VAD 已禁用。");
            return;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[错误] 音频文件不存在：{path}");
            return;
        }

        var wav = WavReader.Read(path);
        var pcm = wav.SampleRate == 16000 ? wav.Pcm16 : AudioUtil.Resample16(wav.Pcm16, wav.SampleRate, 16000);

        const int frameBytes = 1600; // 50ms @16k
        int total = 0, speech = 0, bursts = 0, endpoints = 0;
        bool inSpeech = false;

        for (int offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            int len = Math.Min(frameBytes, pcm.Length - offset);
            var frame = new byte[len];
            Buffer.BlockCopy(pcm, offset, frame, 0, len);

            bool hasSpeech = runtime.Vad.Accept(frame);
            total++;
            if (hasSpeech) speech++;
            if (hasSpeech && !inSpeech) { bursts++; inSpeech = true; }
            else if (!hasSpeech) inSpeech = false;

            if (runtime.Vad.SilenceExceeded)
            {
                endpoints++;
                Console.WriteLine($"  [VAD ] 句尾 #{endpoints}（{offset / 32.0 / 1000:0.00}s 处）");
                runtime.Vad.Reset();
                inSpeech = false;
            }
        }

        double ratio = total == 0 ? 0 : speech * 100.0 / total;
        Console.WriteLine($"  [VAD ] {runtime.Vad.Name} · {Path.GetFileName(path)} {wav.SampleRate}Hz {pcm.Length / 2 / 16000.0:0.00}s");
        Console.WriteLine($"  [VAD ] 帧 {total}（50ms）· 语音帧 {speech}（{ratio:0}%）· 语音突起 {bursts} 次 · 检出句尾 {endpoints} 次");
        Console.WriteLine("  [VAD ] 参考：一段连续话应只检出 1 次句尾；突起次数远大于 1 说明阈值把句子切碎了。");
    }

    /// <summary>
    /// 麦克风交互，两种模式（方案 §5.1）：
    /// - **按键说话**（默认）：按住<span>空格</span>才录音，松开即结束。嘈杂环境必用。
    /// - **免提**（<paramref name="handsFree"/>）：VAD 自动断句，直接说话、停顿即收尾。省事但会误触发。
    /// 两种模式都边说边出 partial 字幕；妹妹说话期间静音上行（半双工）。
    /// </summary>
    public static async Task RunMicAsync(AiRuntime runtime, bool speak, bool handsFree = false,
        string? dumpPath = null, string? logPath = null)
    {
        bool pushToTalk = !handsFree;

        _log?.Dispose();
        _log = SessionLog.Open(logPath ?? "logs/mic_session.log");

        if (!AudioIo.HasInputDevice())
        {
            Console.Error.WriteLine("[错误] 未检测到录音设备。");
            return;
        }

        if (runtime.Asr == null)
        {
            Console.Error.WriteLine("[错误] ASR 未就绪：缺少 sherpa-onnx 流式模型（见 README「准备模型」）。");
            return;
        }

        if (runtime.Asr is not IStreamingAsr streaming)
        {
            Console.Error.WriteLine("[错误] 当前 ASR 后端不支持流式识别。");
            return;
        }

        if (handsFree && runtime.Vad == null)
        {
            Console.Error.WriteLine("[错误] 免提模式需要 VAD 自动断句；请把 vad.backend 设为 auto/silero/energy，或去掉 --free。");
            return;
        }

        int sampleRate = runtime.Asr.SampleRate;
        int deviceNumber = runtime.Config.Audio.DeviceNumber;
        string deviceName = AudioIo.DeviceName(deviceNumber) ?? "系统默认";

        Console.WriteLine(pushToTalk
            ? $"【按键说话】按住 {PttKeyName} 说话，松开即结束本轮；Ctrl+C 退出。"
            : $"【免提】连续聆听中（VAD={runtime.Vad!.Name}）：直接说话，停顿约 {runtime.Config.Vad.MinSilenceDuration}s 自动结束本轮；Ctrl+C 退出。");
        Console.WriteLine($"  录音设备 [{deviceNumber}] {deviceName}（选错设备会听到「幻觉文本」，用 --devices 换设备）");

        Log("SESSION", $"mode={(pushToTalk ? "PTT/空格" : "免提")} speak={speak} device=[{deviceNumber}] {deviceName}");
        Log("SESSION", $"llm={runtime.Llm.Name} asr={runtime.Asr.Name} vad={runtime.Vad?.Name ?? "none"} tts={runtime.Tts.Name}");
        if (_log != null) Console.WriteLine($"  会话日志：{_log.Path}");
        if (dumpPath != null) Log("SESSION", $"录音取证 wav：{dumpPath}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        bool muted = false; // 妹妹说话期间闭嘴，避免把 TTS 回放当成玩家输入（半双工，方案 §6.3）
        using var capture = AudioIo.StartCapture(sampleRate, frame =>
        {
            // 注意：**必须无条件写入 channel**。曾经在 PTT 模式下改成「没按住键就不写」，
            // 结果松键后 channel 永远为空，主循环阻塞在 ReadAsync，再也观测不到「键已松开」这个沿，
            // 表现为「按住说话完全没反应」。半双工只靠 muted 在回合/播放期间丢弃即可。
            if (muted) return;
            channel.Writer.TryWrite(frame);
        }, deviceNumber: deviceNumber);

        var session = streaming.CreateSession();
        string lastPartial = string.Empty;
        bool speaking = false;
        bool awaitingRelease = false; // 已按句尾收尾，等松键后再接受下一次录音
        int turns = 0;
        int heldFrames = 0;
        int idleFrames = 0;
        float peakRms = 0f;
        var dump = dumpPath != null ? new List<byte>() : null;

        async Task FinishTurnAsync()
        {
            Log("REC", $"{heldFrames} 帧（{heldFrames * 0.05:0.00}s）· 峰值 RMS {peakRms:0}"
                       + (peakRms < 100f ? " ⚠ 几乎没有声音" : ""));

            // 取证：按住期间到底录进去多少帧、有没有声音
            Console.WriteLine($"  [录音] {heldFrames} 帧（{heldFrames * 0.05:0.00}s）· 峰值 RMS {peakRms:0}"
                              + (peakRms < 100f ? "  ⚠ 几乎没有声音，检查录音设备/静音开关（--devices）" : ""));

            if (dump != null && dump.Count > 0)
            {
                try
                {
                    AudioIo.SaveWav16(dumpPath!, dump.ToArray(), sampleRate);
                    Console.WriteLine($"  [录音] 已存 {dumpPath}（用播放器听一下：若是杂音/无声 → 采集问题；若清晰但识别错 → ASR 问题）");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [录音] 存 wav 失败：{ex.Message}");
                }
                dump.Clear();
            }

            heldFrames = 0;
            peakRms = 0f;

            var text = session.Finish();
            Log("ASR", $"最终文本：「{text}」");
            session.Dispose();
            session = streaming.CreateSession();
            runtime.Vad?.Reset();
            speaking = false;
            lastPartial = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine("  [ASR ] （未识别到内容）");
                return;
            }

            muted = true;
            while (channel.Reader.TryRead(out _)) { } // 丢掉回合期间排队的帧

            turns++;
            var result = await RunTurnAsync(runtime, text, label: $" 第 {turns} 轮 ·");
            if (speak) SpeakLast(result.Events);

            muted = false;
        }

        try
        {
            while (!cts.IsCancellationRequested)
            {
                byte[] frame;
                try
                {
                    frame = await channel.Reader.ReadAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (pushToTalk)
                {
                    bool down = IsPttKeyDown();

                    if (!down)
                    {
                        awaitingRelease = false;
                        if (speaking)
                        {
                            Log("PTT", "松开 → 收尾本轮");
                            await FinishTurnAsync();
                        }
                        else
                        {
                            idleFrames++;
                            if (idleFrames % 40 == 0)
                            {
                                Log("IDLE", $"待机已收到 {idleFrames} 帧（无按键）· 最近 rms={EnergyVad.Rms(frame):0}");
                            }
                        }
                        continue;
                    }

                    if (!speaking)
                    {
                        if (awaitingRelease) continue; // 已经句尾收尾过，等松键
                        speaking = true;
                        while (channel.Reader.TryRead(out _)) { } // 丢掉按下瞬间的积压
                        Log("PTT", "按下 → 清空积压，开始录音");
                        Console.WriteLine($"  [录音] 录音中…（说完停顿会自动响应；也可松开 {PttKeyName} 立即结束）");
                    }

                    heldFrames++;
                    dump?.AddRange(frame);
                    float rms = EnergyVad.Rms(frame);
                    if (rms > peakRms) peakRms = rms;
                    Log("FRAME", $"#{heldFrames} rms={rms:0}");

                    var partial = session.Accept(frame);
                    if (!string.IsNullOrEmpty(partial) && partial != lastPartial)
                    {
                        Log("PARTIAL", partial);
                        Console.WriteLine($"  [字幕] {partial}");
                        lastPartial = partial;
                    }

                    // 说完就响应：不等松键。
                    // 否则按住期间只有字幕在动、没有任何反馈，用户会以为没生效而**再说一遍**，
                    // 两遍音频被同一个识别会话拼起来 → 识别出「XXX XXX」重复文本 → 判成听不懂。
                    if (session.IsEndpoint)
                    {
                        Log("PTT", $"识别到句尾（{heldFrames} 帧）→ 立刻收尾");
                        awaitingRelease = true;
                        await FinishTurnAsync();
                    }
                    continue;
                }

                // 免提：VAD 决定起止
                bool hasSpeech = runtime.Vad!.Accept(frame);

                if (hasSpeech || speaking)
                {
                    speaking = true;
                    var partial = session.Accept(frame);
                    if (!string.IsNullOrEmpty(partial) && partial != lastPartial)
                    {
                        Console.WriteLine($"  [字幕] {partial}");
                        lastPartial = partial;
                    }
                }

                if (runtime.Vad.SilenceExceeded) await FinishTurnAsync();
            }
        }
        finally
        {
            session.Dispose();
            Log("SESSION", "已停止聆听");
            Console.WriteLine("已停止聆听。");
        }
    }

    // ---- 按键说话（PTT）：轮询按键的全局按下状态，拿到「按住/松开」语义 ----
    // 注意：用的是全局键状态，控制台窗口没焦点时按键也会触发录音。

    private const int VkSpace = 0x20; // VK_SPACE
    private const string PttKeyName = "空格";

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsPttKeyDown() => (GetAsyncKeyState(VkSpace) & 0x8000) != 0;

    /// <summary>直接让妹妹说一句（走 TTS 双路由），用于在无 ASR 时验证音频输出链路。</summary>
    public static void RunPlay(AiRuntime runtime, string text)
    {
        var result = runtime.Tts.Route(null, text, Intents.QuestionBack, runtime.Pipeline.Trust.Emotion);
        if (result.Pcm16 == null)
        {
            Console.Error.WriteLine("[错误] 无可用音频：语音库未命中且实时合成不可用。");
            return;
        }

        Console.WriteLine($"小雅> {result.Text}");
        Console.WriteLine($"  [TTS ] ({(result.FromVoiceLib ? "语音库" : "实时合成")}) 回放 {result.Pcm16.Length / 2 / (double)result.SampleRate:0.00}s…");
        AudioIo.Play(result.Pcm16, result.SampleRate);
    }

    /// <summary>
    /// 用**当前实时合成引擎**重录语音库（`voice-corpus/lines.csv` → `wavs/&lt;id&gt;.wav`）。
    ///
    /// 为什么需要它：`TtsRouter` 的 ①「语音库精确命中」与 ②「实时合成」是两条不同来源，
    /// **只有两者用同一个引擎时，妹妹才是一个嗓门**。换引擎（SAPI ↔ VITS/melo-tts）后必须重录，
    /// 否则库内台词一个声音、自由句另一个声音 —— 这正是日志 T 里选 SAPI 的那条理由。
    /// 用它就不必再依赖 `tools/voicelib_synth.py`（那个走 Windows SAPI），语料永远跟当前引擎一致。
    /// </summary>
    public static void RunSynthVoiceLib(AiRuntime runtime)
    {
        if (runtime.RealtimeTts == null)
        {
            Console.Error.WriteLine("[错误] 实时合成不可用（tts.backend=voicelib？），无法重录语料。");
            return;
        }

        var manifest = runtime.Config.ResolvePath(runtime.Config.Tts.VoicelibManifest);
        var corpusDir = Path.GetDirectoryName(manifest) ?? "voice-corpus";
        var csvPath = Path.Combine(corpusDir, "lines.csv");
        var wavDir = Path.Combine(corpusDir, "wavs");

        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"[错误] 语料不存在：{csvPath}");
            return;
        }

        var rows = ReadCorpus(csvPath);
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"[错误] 语料为空：{csvPath}");
            return;
        }

        Directory.CreateDirectory(wavDir);
        Console.WriteLine($"  引擎：{runtime.RealtimeTts.Name} · 采样率 {runtime.RealtimeTts.SampleRate}Hz");
        Console.WriteLine($"  语料：{rows.Count} 条 → {wavDir}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        int ok = 0;
        var failed = new List<string>();

        foreach (var (id, text) in rows)
        {
            var pcm = runtime.RealtimeTts.Synthesize(text);
            if (pcm == null || pcm.Length == 0)
            {
                failed.Add(id);
                continue;
            }
            AudioIo.SaveWav16(Path.Combine(wavDir, id + ".wav"), pcm, runtime.RealtimeTts.SampleRate);
            ok++;
            Console.WriteLine($"  [合成] {id} {pcm.Length / 2 / (double)runtime.RealtimeTts.SampleRate:0.0}s  {text}");
        }

        watch.Stop();
        Console.WriteLine($"[重录] 成功 {ok}/{rows.Count} · 耗时 {watch.Elapsed.TotalSeconds:0.0}s · 引擎 {runtime.RealtimeTts.Name}");
        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"[重录] 失败 {failed.Count} 条：{string.Join(",", failed)}");
        }

        WarnIfManifestMissing(rows, manifest);
    }

    /// <summary>
    /// 重录只写 wav，**manifest 得由 <c>tools/voicelib_gen.py</c> 刷新**（单一生成器，不在这里再实现一份）。
    /// 新增 id 时忘了刷 manifest，游戏里就找不到那条语音 —— 而且**没有任何报错**，所以这里必须主动吼一声。
    /// </summary>
    private static void WarnIfManifestMissing(List<(string Id, string Text)> rows, string manifestPath)
    {
        if (!File.Exists(manifestPath)) return;

        var missing = new List<string>();
        try
        {
            var loaded = VoiceLib.Load(manifestPath);
            foreach (var (id, _) in rows)
            {
                if (loaded.FindById(id) == null) missing.Add(id);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[重录] 读 manifest 失败：{ex.Message}");
            return;
        }

        if (missing.Count == 0) return;

        Console.Error.WriteLine($"[重录] ⚠️ manifest 里没有这些 id：{string.Join(",", missing)}");
        Console.Error.WriteLine("       合成只写了 wav —— 请刷新 manifest，否则游戏里找不到这些语音：");
        Console.Error.WriteLine("       python tools\\voicelib_gen.py");
    }

    /// <summary>读 `lines.csv` 的 id/text 两列（表头跳过；台词里不含 ASCII 逗号，直接按逗号切即可）。</summary>
    private static List<(string Id, string Text)> ReadCorpus(string csvPath)
    {
        var rows = new List<(string, string)>();
        foreach (var line in File.ReadAllLines(csvPath, System.Text.Encoding.UTF8))
        {
            if (line.Length == 0 || line.StartsWith("id,", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            var id = parts[0].Trim();
            var text = parts[1].Trim();
            if (id.Length > 0 && text.Length > 0) rows.Add((id, text));
        }
        return rows;
    }

    private static void SpeakLast(IReadOnlyList<TurnEvent> events)
    {
        var tts = events.LastOrDefault(e => e.Kind == TurnEventKind.TtsAudio);
        if (tts?.Pcm16 is { Length: > 0 })
        {
            AudioIo.Play(tts.Pcm16, tts.SampleRate);
        }
    }

    public static async Task RunInteractiveAsync(AiRuntime runtime)
    {
        Console.WriteLine("输入指令回车发送；Ctrl+C 或 /quit 退出。");
        int turn = 0;
        while (true)
        {
            Console.Write("你> ");
            var line = Console.ReadLine();
            if (line == null) break;

            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("/", StringComparison.Ordinal))
            {
                if (HandleSlashCommand(runtime, line)) break;
                continue;
            }

            turn++;
            await RunTurnAsync(runtime, line, label: $" 第 {turn} 回合 ·");
        }
    }

    /// <summary>打印 LLM 后端的 prefill / 生成分解（后端未实现诊断接口时静默）。</summary>
    public static void PrintLlmDiagnostics(AiRuntime runtime)
    {
        if (runtime.Llm is ILlmDiagnostics diag && diag.LastTurnSummary is { Length: > 0 } summary)
        {
            Console.WriteLine($"  [LLM ] {summary}");
        }
    }

    private static bool HandleSlashCommand(AiRuntime runtime, string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/quit":
            case "/exit":
                return true;

            case "/health":
                Console.WriteLine($"  [健康] llm={runtime.Llm.Name} asr={runtime.Asr?.Name ?? "none"} vad={runtime.Vad?.Name ?? "none"} tts={runtime.Tts.Name} v={AiRuntime.Version}");
                Console.WriteLine($"         llm: {runtime.LlmDetail}");
                Console.WriteLine($"         asr: {runtime.AsrDetail}");
                Console.WriteLine($"         vad: {runtime.VadDetail}");
                Console.WriteLine($"         tts: {runtime.TtsDetail}");
                return false;

            case "/state":
                var t = runtime.Pipeline.Trust;
                Console.WriteLine($"  [状态] 信任 {t.Trust:0}/100 情绪={EmotionNames.ToWire(t.Emotion)}");
                return false;

            case "/event":
                if (parts.Length < 2)
                {
                    Console.WriteLine("  用法：/event collision|red_light|lane_departure|hard_brake|overspeed|smooth_drive|clean_pass");
                    return false;
                }
                var ev = runtime.Pipeline.ApplyLocalEvent(parts[1].Trim());
                Console.WriteLine($"  [事件] {parts[1].Trim()} 信任变化 {ev.State!.TrustDelta:+0;-0;0} → {ev.State.Trust:0}/100 情绪={EmotionNames.ToWire(ev.State.Emotion)}");
                return false;

            default:
                Console.WriteLine($"  未知命令：{parts[0]}");
                return false;
        }
    }

    public static void PrintEvents(IReadOnlyList<TurnEvent> events)
    {
        foreach (var e in events) PrintEvent(e);
        Console.WriteLine();
    }

    /// <summary>打印单个事件（流式路径边产出边打印）。</summary>
    public static void PrintEvent(TurnEvent e)
    {
        Log("EVENT", DescribeEvent(e));

        switch (e.Kind)
        {
            case TurnEventKind.AsrPartial:
                Console.WriteLine($"  [字幕] {e.Text}");
                break;

            case TurnEventKind.AsrFinal:
                Console.WriteLine($"  [玩家] {e.Text}{(e.LowConfidence ? "  (低置信)" : string.Empty)}");
                break;

            case TurnEventKind.DriveAction:
                var a = e.Action!;
                Console.WriteLine($"  [动作] {IntentLabel(a.Intent)} steering={a.Steering:0.00} throttle={a.Throttle:0.00} brake={a.Brake:0.00} hold={a.HoldMs}ms");
                break;

            case TurnEventKind.LlmDelta:
                Console.WriteLine($"小雅> {e.Text}");
                break;

            case TurnEventKind.TtsAudio:
                string tag = e.FromVoiceLib ? "语音库" : (e.Pcm16 is { Length: > 0 } ? "实时合成" : "仅字幕");
                string dur = e.Pcm16 is { Length: > 0 } p && e.SampleRate > 0
                    ? $" {p.Length / 2 / (double)e.SampleRate:0.00}s"
                    : string.Empty;
                Console.WriteLine($"  [TTS ] ({tag}){dur}");
                break;

            case TurnEventKind.StateDelta:
                var s = e.State!;
                Console.WriteLine($"  [状态] 信任 {s.Trust:0}/100 ({s.TrustDelta:+0;-0;0}) 情绪={EmotionNames.ToWire(s.Emotion)}");
                break;

            case TurnEventKind.Error:
                Console.Error.WriteLine($"  [错误] {e.ErrorCode}: {e.ErrorMessage}");
                break;
        }
    }

    /// <summary>把事件压成一行，便于写日志后排查（方案 §8 的「事件流可追溯」）。</summary>
    private static string DescribeEvent(TurnEvent e)
    {
        switch (e.Kind)
        {
            case TurnEventKind.AsrPartial:
            case TurnEventKind.AsrFinal:
                return $"{e.Kind} text=「{e.Text}」 conf={e.Confidence:0.00}";
            case TurnEventKind.LlmDelta:
                return $"LlmDelta reply=「{e.Text}」";
            case TurnEventKind.DriveAction:
                var a = e.Action!;
                return $"DriveAction intent={a.Intent} steer={a.Steering:0.00} thr={a.Throttle:0.00} brake={a.Brake:0.00} hold={a.HoldMs}ms conf={a.Confidence:0.00}";
            case TurnEventKind.TtsAudio:
                return $"TtsAudio fromVoicelib={e.FromVoiceLib} pcm={e.Pcm16?.Length ?? 0}B rate={e.SampleRate} text=「{e.Text}」";
            case TurnEventKind.StateDelta:
                var s = e.State!;
                return $"StateDelta trust={s.Trust:0} delta={s.TrustDelta:+0;-0;0} emotion={s.Emotion}";
            case TurnEventKind.Error:
                return $"Error {e.ErrorCode}: {e.ErrorMessage}";
            default:
                return e.Kind.ToString();
        }
    }

    private static string IntentLabel(string intent) => intent switch
    {
        Intents.TurnLeft => "↰左转",
        Intents.TurnRight => "↱右转",
        Intents.Accelerate => "↑加速",
        Intents.Brake => "⏹刹车",
        Intents.LaneChange => "⇄变道",
        Intents.Park => "⏸停车",
        Intents.Reverse => "↩倒车",
        Intents.Ignore => "✖拒绝",
        Intents.QuestionBack => "？反问",
        _ => intent,
    };
}