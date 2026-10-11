using System.Text;
using Copilot.Core;
using Copilot.Core.Llm;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Copilot.AIService.Llm;

/// <summary>
/// LLM 后端首选实现：LLamaSharp + Vulkan（IT方案1.md V2.0 §3 / §5.2）。
///
/// **有状态会话**：持有常驻 <see cref="LLamaContext"/> + <see cref="InteractiveExecutor"/>，
/// 只把「每回合新增的文本」喂进去，让 KV cache 跨回合复用。
/// 这是延迟的关键——早先用无状态的 StatelessExecutor 时，每个回合都要把
/// 「人格 + 规则 + 示例 + 状态 + 记忆」整段重新 prefill（实测 ~1.4s，见开发进度 §7-1）。
///
/// 模型文件缺失或原生后端加载失败时 <see cref="TryCreate"/> 返回 null，由工厂降级到
/// <see cref="RuleLlmBackend"/>。
/// </summary>
public sealed class LlamaSharpBackend : ILlmBackend, IStreamingLlmBackend, ILlmDiagnostics, IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;
    private readonly int _maxTokens;
    private readonly float _temperature;
    private readonly bool _debugRawOutput;

    /// <summary>GBNF 约束解码用的文法；未启用或原生解析失败时为 null（不阻塞推理）。</summary>
    private readonly Grammar? _grammar;

    /// <summary>gRPC 服务端可能并发调用，单会话必须串行。</summary>
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private bool _sessionStarted;

    public string Name => "llamasharp";
    public bool IsAvailable => true;

    /// <summary>会话是否已经预热（首回合之后为 true）。</summary>
    public bool SessionWarm => _sessionStarted;

    /// <summary>最近一回合的耗时分解（prefill / 生成 / tok/s）。</summary>
    public string? LastTurnSummary { get; private set; }

    private LlamaSharpBackend(LLamaWeights weights, LLamaContext context, InteractiveExecutor executor,
        int maxTokens, float temperature, bool debugRawOutput, Grammar? grammar, string grammarStatus)
    {
        _weights = weights;
        _context = context;
        _executor = executor;
        _maxTokens = maxTokens;
        _temperature = temperature;
        _debugRawOutput = debugRawOutput;
        _grammar = grammar;
        GrammarStatus = grammarStatus;
    }

    /// <summary>约束解码状态（供 --health / 日志展示）。</summary>
    public string GrammarStatus { get; }

    public static LlamaSharpBackend? TryCreate(Config.LlmOptions options, string modelPath, out string reason)
    {
        if (!File.Exists(modelPath))
        {
            reason = $"模型不存在：{modelPath}";
            return null;
        }

        ConfigureNativeLogging(options.VerboseLogs);

        LLamaWeights? weights = null;
        LLamaContext? context = null;
        try
        {
            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = (uint)options.ContextSize,
                GpuLayerCount = options.GpuLayers,
                Threads = options.Threads,
                BatchSize = options.BatchSize,
                FlashAttention = options.FlashAttention,
                UseMemorymap = options.UseMemorymap,
            };

            weights = LLamaWeights.LoadFromFile(modelParams);
            context = weights.CreateContext(modelParams, NullLogger.Instance);
            var executor = new InteractiveExecutor(context);

            // GBNF 约束解码。文法解析失败不应该拖垮整个后端 → 降级为「不约束」，只记录状态。
            Grammar? grammar = null;
            string grammarStatus = "off";
            if (options.UseGrammar)
            {
                try
                {
                    // LLamaSharp 0.23 的 Grammar 构造函数要求显式给根规则名（无默认值），
                    // 且该版本 Grammar 不实现 IDisposable（无原生句柄需手工释放）。
                    grammar = new Grammar(OutputGrammar.Gbnf, "root");
                    grammarStatus = $"gbnf（intent 锁定 {Intents.All.Count} 个枚举值）";
                }
                catch (Exception gex)
                {
                    grammarStatus = $"gbnf 解析失败，已降级不约束：{gex.Message}";
                }
            }

            reason = string.Empty;
            return new LlamaSharpBackend(weights, context, executor, options.MaxTokens, options.Temperature,
                options.DebugRawOutput, grammar, grammarStatus);
        }
        catch (Exception ex)
        {
            context?.Dispose();
            weights?.Dispose();
            reason = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// llama.cpp 的原生日志默认直接打到 stderr（加载时上百行），会淹没 CLI 输出。
    /// 默认改为静默；需要排查推理问题时把 appsettings.json 的 llm.verboseLogs 打开。
    /// </summary>
    public static void ConfigureNativeLogging(bool verbose)
    {
        NativeLogConfig.llama_log_set((level, message) =>
        {
            if (verbose) Console.Error.WriteLine($"[llama:{level}] {message}");
        });
    }

    /// <summary>
    /// 组装本回合要喂给执行器的文本（Qwen2.5 chat 模板）。
    /// <paramref name="systemPrompt"/> 只在首回合下发一次；之后每回合仅发增量，
    /// 前面已 eval 过的 token 由 KV cache 复用。
    /// </summary>
    internal string BuildTurnText(LlmRequest request)
    {
        var sb = new StringBuilder();

        if (!_sessionStarted)
        {
            sb.Append("<|im_start|>system\n").Append(request.SystemPrompt).Append("<|im_end|>\n");
            _sessionStarted = true;
        }
        else
        {
            // 上一轮的 assistant 内容停在 AntiPrompt 之前（<|im_end|> 未被生成），这里补上收尾
            sb.Append("<|im_end|>\n");
        }

        sb.Append("<|im_start|>user\n").Append(request.UserText).Append("<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    public async Task<LlmOutput> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        LlmOutput? result = null;
        await foreach (var chunk in GenerateStreamAsync(request, ct).ConfigureAwait(false))
        {
            if (chunk.Final is { } final) result = final;
        }
        return result ?? LlmJson.Parse(null);
    }

    /// <summary>
    /// 流式生成：token 逐个累积，**action 对象的右花括号一出现就立即 yield 出去**
    /// （此时 reply 还在生成），整段结束后再 yield 最终解析结果。
    /// </summary>
    public async IAsyncEnumerable<LlmStreamChunk> GenerateStreamAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sb = new StringBuilder();
            bool actionSent = false;
            int closeBraces = 0;
            long firstTokenMs = -1;
            int tokens = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await foreach (var token in _executor.InferAsync(BuildTurnText(request), BuildInferenceParams(), ct)
                               .ConfigureAwait(false))
            {
                if (firstTokenMs < 0) firstTokenMs = stopwatch.ElapsedMilliseconds;
                tokens++;
                sb.Append(token);

                // 只要已经出现过右花括号就尝试抽取 action。
                // 注意：**不能**用「缓冲区最后一个字符是 }」当守卫 —— llama.cpp 会把
                // `},"tts_hint"` 打成一个多字符 token，那样条件永远不成立（踩过的坑）。
                closeBraces += CountCloseBraces(token);
                if (!actionSent && closeBraces > 0
                    && LlmJson.TryExtractAction(sb.ToString(), out var readyAction))
                {
                    actionSent = true;
                    yield return new LlmStreamChunk { ActionReady = readyAction };
                }
            }

            stopwatch.Stop();
            RecordStats(firstTokenMs, tokens, stopwatch.ElapsedMilliseconds);

            if (_debugRawOutput) Console.Error.WriteLine($"[raw] {sb}");

            yield return new LlmStreamChunk { Final = LlmJson.Parse(sb.ToString()) };
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private InferenceParams BuildInferenceParams() => new InferenceParams
    {
        MaxTokens = _maxTokens,
        AntiPrompts = new List<string> { "<|im_end|>", "<|im_start|>" },
        SamplingPipeline = new DefaultSamplingPipeline
        {
            Temperature = _temperature,
            TopK = 40,
            TopP = 0.95f,
            Grammar = _grammar,
        },
    };

    private void RecordStats(long firstTokenMs, int tokens, long totalMs)
    {
        double decodeMs = Math.Max(1, totalMs - Math.Max(firstTokenMs, 0));
        double tokPerSec = tokens * 1000.0 / decodeMs;
        LastTurnSummary =
            $"prefill {Math.Max(firstTokenMs, 0)}ms · 生成 {tokens} tok / {decodeMs:0}ms = {tokPerSec:0} tok/s · 合计 {totalMs}ms";
    }

    private static int CountCloseBraces(string token)
    {
        int n = 0;
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] == '}') n++;
        }
        return n;
    }

    public void Dispose()
    {
        // InteractiveExecutor 不持有原生资源，上下文与权重才是要释放的。
        // 文法（LLamaSharp 0.23 的 Grammar）不是 IDisposable，随上下文一并释放。
        _context.Dispose();
        _weights.Dispose();
        _turnLock.Dispose();
    }
}

/// <summary>
/// LLM 后端工厂：auto 时按 llamasharp → rule 逐级降级（方案 §5.2 降级矩阵）。
/// </summary>
public static class LlmBackendFactory
{
    public static ILlmBackend Create(Config config, out string detail)
    {
        var backend = (config.Llm.Backend ?? "auto").Trim().ToLowerInvariant();

        if (backend == "rule")
        {
            detail = "显式指定规则后端";
            return new RuleLlmBackend();
        }

        var modelPath = config.ModelFile(config.Llm.ModelPath);
        var llm = LlamaSharpBackend.TryCreate(config.Llm, modelPath, out var reason);

        if (llm != null)
        {
            detail = $"llamasharp（{Path.GetFileName(modelPath)}，ctx={config.Llm.ContextSize}，ngl={config.Llm.GpuLayers}，有状态会话，{llm.GrammarStatus}）";
            return llm;
        }

        if (backend == "llamasharp")
        {
            throw new InvalidOperationException($"指定 llamasharp 后端但不可用：{reason}");
        }

        detail = $"降级 rule（{reason}）";
        return new RuleLlmBackend();
    }
}