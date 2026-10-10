using System;
using System.Text.RegularExpressions;

namespace Copilot.Core.Llm
{
    /// <summary>
    /// 归一化 LLM 输出：把台词库 URI 收拢到 <see cref="LlmOutput.TtsHint"/>，
    /// 并把 reply 回填成台词原文（字幕不再显示 voicelib://xxx）。
    /// 对应 V1.0 的 sanitize_output 行为（IT方案1.md V2.0 §5.5）。
    /// </summary>
    public static class LlmOutputSanitizer
    {
        private const string Prefix = "voicelib://";
        private static readonly Regex UriInText = new Regex(
            @"voicelib://[A-Za-z0-9_\-\.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static LlmOutput Sanitize(LlmOutput output, VoiceLib? lib)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            // ① reply 里夹带了 URI → 提取到 tts_hint
            if (!string.IsNullOrEmpty(output.Reply))
            {
                var m = UriInText.Match(output.Reply);
                if (m.Success)
                {
                    output.TtsHint = string.IsNullOrEmpty(output.TtsHint) ? m.Value : output.TtsHint;
                    var cleaned = UriInText.Replace(output.Reply, string.Empty).Trim();
                    output.Reply = cleaned;
                }
            }

            if (string.IsNullOrEmpty(output.TtsHint)) return output;

            // ② tts_hint 归一化（容忍写成裸 id）
            var hint = output.TtsHint!.Trim();
            if (!hint.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                hint = Prefix + hint;
            output.TtsHint = hint;

            // ③ reply 为空时回填台词原文
            if (string.IsNullOrWhiteSpace(output.Reply) && lib != null)
            {
                var id = hint.Substring(Prefix.Length);
                var entry = lib.FindById(id);
                if (entry != null) output.Reply = entry.Text;
            }

            return output;
        }
    }
}