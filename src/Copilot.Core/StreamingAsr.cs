using System;

namespace Copilot.Core
{
    /// <summary>
    /// 流式识别会话（IT方案1.md V2.0 §5.1「ASR 中间结果实时上屏字幕」）。
    /// 麦克风持续喂音频，<see cref="Accept"/> 返回当前 partial 文本用于实时字幕；
    /// <see cref="Finish"/> 收尾给出最终文本。
    /// </summary>
    public interface IAsrSession : IDisposable
    {
        /// <summary>喂入一段 16kHz 单声道 16-bit PCM，返回当前 partial 文本（可能为空串）。</summary>
        string Accept(byte[] pcm16);

        /// <summary>识别器是否判定这句已经结束（端点检测）。</summary>
        bool IsEndpoint { get; }

        /// <summary>结束输入并返回最终文本。</summary>
        string Finish();
    }

    /// <summary>支持流式识别的 ASR 后端（partial 字幕 + 端点检测）。</summary>
    public interface IStreamingAsr
    {
        IAsrSession CreateSession();
    }
}