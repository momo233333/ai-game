using System;

namespace Copilot.Core.Llm
{
    /// <summary>
    /// 输出 JSON 的 GBNF 文法（llama.cpp 约束解码，IT方案1.md §3 "约束解码"）。
    ///
    /// 目标结构（与 <see cref="Dialogue.PromptBuilder.Rules"/> 的示例一致）：
    /// <code>
    /// {"action":{"intent":"TURN_LEFT"},"tts_hint":null,"reply":"知道了，左转交给我。","trust_delta":0}
    /// </code>
    ///
    /// 解决的问题：
    /// ① 字段名拼错 / 少字段 / 输出 Markdown 代码块 / JSON 前后夹解释文字 —— 从**解码层**杜绝，
    ///    而不是靠 <see cref="LlmJson"/> 事后容错；
    /// ② <c>intent</c> 只能取 <see cref="Intents.All"/> 里的枚举值，杜绝编造意图；
    /// ③ 结构固定后 <c>action</c> 永远是第一个字段，**流式下它必然最早闭合**，
    ///    「说完→车动」的提前下发不再依赖模型自愿把 action 写在前面。
    ///
    /// 注意：文法**不校验数值合法性**（steering/throttle 等仍由 <see cref="ActionLimits.Canonical"/> 决定），
    /// 也不做语义校验（危险指令判定在 <see cref="Intents.IsDangerousCommand"/>）。
    /// </summary>
    public static class OutputGrammar
    {
        // ---- GBNF 字面量助手（GBNF 里字符串用双引号包，内部的双引号要写成 \"）----

        /// <summary>
        /// GBNF 字面量，**内容为裸文本**（不含引号）：Lit("null") → GBNF <c>"null"</c> → 匹配 <c>null</c>。
        /// 只用于 JSON 里本来就裸写的东西（null / 数字 / 标点）。
        /// </summary>
        private static string Lit(string text) => "\"" + text + "\"";

        /// <summary>
        /// GBNF 字面量，**内容含双引号**：JsonKey("intent") → GBNF <c>"\"intent\""</c> → 匹配 <c>"intent"</c>。
        /// JSON 里所有字符串（字段名**和**字符串取值）都必须用它，否则会生成裸标识符这种非法 JSON
        /// （踩过的坑：intent 用了 Lit，模型输出 <c>"intent": TURN_LEFT</c> 少了引号）。
        /// </summary>
        private static string JsonKey(string name) => "\"\\\"" + name + "\\\"\"";

        private const string LBrace = "\"{\"";
        private const string RBrace = "\"}\"";
        private const string Colon = "\":\"";
        private const string Comma = "\",\"";

        /// <summary>GBNF 中表示单个双引号字符："\""</summary>
        private static readonly string Quote = Lit("\\\"");

        /// <summary>intent 的取值分支，直接从 <see cref="Intents.All"/> 生成 —— 保证文法与代码不会漂移。</summary>
        private static string IntentAlternatives()
        {
            var parts = new string[Intents.All.Count];
            for (int i = 0; i < Intents.All.Count; i++) parts[i] = JsonKey(Intents.All[i]);
            return string.Join(" | ", parts);
        }

        /// <summary>完整的 GBNF 文本（每行一条规则）。</summary>
        public static readonly string Gbnf = string.Join("\n", new[]
        {
            // 顶层：字段顺序固定为 action → tts_hint → reply → trust_delta
            "root     ::= " + LBrace + " ws " + JsonKey("action") + " ws " + Colon + " ws act ws "
                + Comma + " ws " + JsonKey("tts_hint") + " ws " + Colon + " ws hint ws "
                + Comma + " ws " + JsonKey("reply") + " ws " + Colon + " ws rep ws "
                + Comma + " ws " + JsonKey("trust_delta") + " ws " + Colon + " ws num ws " + RBrace,

            // action 里只有 intent 一个字段（数值一律由程序决定）
            "act      ::= " + LBrace + " ws " + JsonKey("intent") + " ws " + Colon + " ws intent ws " + RBrace,

            "intent   ::= " + IntentAlternatives(),

            // tts_hint：语音库 id 或 null
            "hint     ::= " + Lit("null") + " | rep",

            // JSON 字符串
            "rep      ::= " + Quote + " ( [^\"\\\\] | " + Lit("\\\\") + " esc )* " + Quote,
            "esc      ::= [\"\\\\/bfnrt] | " + Lit("u") + " [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]",

            // 数字（trust_delta 允许整数或小数、可带负号）
            "num      ::= \"-\"? [0-9]+ (\".\" [0-9]+)?",

            // 允许字段之间出现空白/换行，避免模型想美化排版时被卡死
            "ws       ::= [ \\t\\n\\r]*",
        });

        /// <summary>是否启用了文法约束（供自检/日志用）。</summary>
        public static bool ContainsAllIntents()
        {
            for (int i = 0; i < Intents.All.Count; i++)
            {
                if (Gbnf.IndexOf(JsonKey(Intents.All[i]), StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }
    }
}