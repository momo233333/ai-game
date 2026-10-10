using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Copilot.Core.Llm
{
    /// <summary>LLM 结构化输出（对应 proto 的输出契约 actions/reply/tts_hint/trust_delta）。</summary>
    public sealed class LlmOutput
    {
        public DriveAction Action { get; set; } = new DriveAction();
        public string Reply { get; set; } = string.Empty;

        /// <summary>语音库命中提示，形如 "voicelib://L001"；未命中为 null。</summary>
        public string? TtsHint { get; set; }

        public float TrustDelta { get; set; }
    }

    /// <summary>一次生成的输入：玩家原话 + 渲染好的 Prompt + 当前状态。</summary>
    public sealed class LlmRequest
    {
        /// <summary>玩家原话（规则后端只认这个，绝不能用 Prompt，否则会匹配到规则示例词）。</summary>
        public string PlayerText { get; set; } = string.Empty;

        /// <summary>渲染后的完整 Prompt（日志/调试用）。</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>系统回合内容（真实 LLM 后端走 chat 模板时用）。</summary>
        public string SystemPrompt { get; set; } = string.Empty;

        /// <summary>用户回合内容（真实 LLM 后端走 chat 模板时用）。</summary>
        public string UserText { get; set; } = string.Empty;

        public GameStateSnapshot State { get; set; } = new GameStateSnapshot();
    }

    /// <summary>
    /// LLM 后端抽象（IT方案1.md V2.0 §5.2）。
    /// 首选 LLamaSharp（Vulkan）；模型/原生库缺失时降级到 RuleLlmBackend，
    /// 保证语音交互闭环在无模型环境下也能跑通与回归。
    /// </summary>
    public interface ILlmBackend
    {
        /// <summary>后端名（写入 Health.llm_backend，便于诊断降级状态）。</summary>
        string Name { get; }

        /// <summary>是否已加载模型（false 表示处于降级路径）。</summary>
        bool IsAvailable { get; }

        Task<LlmOutput> GenerateAsync(LlmRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// 流式生成的片段（IT方案1.md §4.2：action 字段排在 reply 之前生成，
    /// 「指令先执行、台词后播放」——所以 action 一闭合就要能立刻拿到）。
    /// </summary>
    public sealed class LlmStreamChunk
    {
        /// <summary>action 对象一闭合就带出来（整个回合只出现一次，此时 reply 还没生成完）。</summary>
        public DriveAction? ActionReady { get; set; }

        /// <summary>生成结束时的完整解析结果（整个回合只出现一次）。</summary>
        public LlmOutput? Final { get; set; }
    }

    /// <summary>
    /// 可选的流式后端。实现它，<see cref="TurnPipeline"/> 就能在 LLM 刚生成完 action 时
    /// 立刻下发 drive_action，而不必等整句台词生成完（体感延迟 ~700ms → ~350ms）。
    /// </summary>
    public interface IStreamingLlmBackend
    {
        IAsyncEnumerable<LlmStreamChunk> GenerateStreamAsync(LlmRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// 可选的延迟诊断（方案 §8「性能测试：各环节延迟火焰图」）。
    /// 后端实现它就能被 CLI / 服务器打印出 prefill 与生成的真实耗时。
    /// </summary>
    public interface ILlmDiagnostics
    {
        /// <summary>最近一回合的耗时摘要；尚无数据时为 null。</summary>
        string? LastTurnSummary { get; }
    }
}