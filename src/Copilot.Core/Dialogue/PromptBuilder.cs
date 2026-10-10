using System;
using System.Text;

namespace Copilot.Core.Dialogue
{
    /// <summary>
    /// 系统 Prompt 组装（IT方案1.md V2.0 §7.1）。
    /// 人格描述与 GameState 分离注入；语音库清单以 voicelib:// 形式提供给 LLM 引用。
    /// </summary>
    public static class PromptBuilder
    {
        public const string Persona =
            "你是林小雨，21岁，傲娇毒舌但车技稳，最烦指令不清不楚的人。";

        /// <summary>
        /// 规则块。要点：
        /// ① 正常指令必须映射到对应 intent，IGNORE 只留给危险/不可能指令（实测 7B 会把「靠边停车」也拒了）；
        /// ② **action 只写 intent**——数值全由 <see cref="ActionLimits.Canonical"/> 决定。
        ///    这不只是「别瞎编数值」：action 越短，流式下它就越早闭合，指令越早下发
        ///    （实测动作到达 510ms → ~300ms，见开发进度 §8）；
        /// ③ 给一个完整示例，比纯规则描述可靠得多。
        /// </summary>
        public static readonly string Rules =
            "[规则1] 只输出一个 JSON 对象，前后不要任何解释文字、不要 Markdown 代码块。\n" +
            "[规则2] action.intent 只能取以下之一：TURN_LEFT/TURN_RIGHT/ACCELERATE/BRAKE/LANE_CHANGE/PARK/IGNORE/QUESTION_BACK\n" +
            "[规则3] 正常的驾驶指令必须映射到对应的 intent（左转→TURN_LEFT，靠边停车→PARK，等等）。\n" +
            "        只有「闯红灯/逆行/撞人/酒驾/飙车」这类危险或物理上不可能的指令才用 IGNORE，并把 trust_delta 设为 -5。\n" +
            "[规则4] 没听清或与驾驶无关时用 QUESTION_BACK，trust_delta 保持 0。\n" +
            "[规则5] reply ≤ 40 字，口语化，可用\"哥/诶/啧\"等语气词，禁止书面语，禁止脏话和人身攻击。\n" +
            "[规则6] action 里**只写 intent 一个字段**，不要写 steering/throttle/brake/hold_ms/confidence，具体数值由程序决定。\n" +
            "[示例] 玩家说「前面路口左转」，你输出：\n" +
            "{\"action\":{\"intent\":\"TURN_LEFT\"},\"tts_hint\":null,\"reply\":\"知道了，左转交给我。\",\"trust_delta\":0}\n";

        /// <summary>
        /// **静态**系统 Prompt（人格 + 规则 + 示例），任何回合都不变。
        /// 它是 KV cache 复用的前缀：只要这段不变，后续回合就只需 prefill 新增的
        /// <see cref="BuildTurnInput"/>（IT方案1.md §6.5 延迟预算的关键）。
        /// </summary>
        public static readonly string StaticSystem = "[身份] " + Persona + "\n" + Rules;

        /// <summary>
        /// **每回合变化**的部分（当前状态 + 记忆 + 玩家本轮输入）。
        /// 必须排在静态前缀之后，且不要回头修改静态前缀，否则 KV 失效。
        /// </summary>
        public static string BuildTurnInput(GameStateSnapshot state, DialogueMemory? memory, string? playerText)
        {
            var sb = new StringBuilder();
            sb.Append(StateBlock(state));
            if (memory != null) sb.Append(memory.BuildContextBlock()).Append('\n');
            sb.Append("[本轮玩家] ").Append(playerText ?? string.Empty);
            return sb.ToString();
        }

        /// <summary>拼接版完整 Prompt：给规则后端/日志/调试用（真实 LLM 后端走「静态 + 增量」两段）。</summary>
        public static string BuildFull(GameStateSnapshot state, DialogueMemory? memory, string? playerText)
            => StaticSystem + BuildTurnInput(state, memory, playerText);

        /// <summary>GameState 模板化注入（方案 §5.3）。</summary>
        public static string StateBlock(GameStateSnapshot state)
        {
            if (state == null) return string.Empty;
            var tele = state.Telemetry ?? new VehicleTelemetry();
            var obstacles = string.IsNullOrWhiteSpace(tele.ObstacleSummary) ? "路况正常" : tele.ObstacleSummary;
            return string.Format(
                "[当前状态] 当前车速 {0:0}km/h，{1}。你的情绪：{2}。你对哥哥的信任：{3:0}/100。\n",
                tele.SpeedKmh, obstacles, EmotionNames.ToWire(state.Emotion), state.Trust);
        }
    }
}