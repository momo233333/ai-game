using System;
using System.Text;

namespace Copilot.Core.Dialogue
{
    /// <summary>
    /// 系统 Prompt 组装（IT方案1.md V2.0 §7.1）。
    /// 人格描述与 GameState 分离注入；语音库清单以 voicelib:// 形式提供给 LLM 引用。
    ///
    /// **《驾考宝典》人设**：玩家是坐在**副驾驶的教练**，LLM 扮演**坐在驾驶位的新学员**。
    /// 所以这里的规则要读成「学员听教练的指令，然后做出动作 + 用一句话回应」，
    /// 而不是「副驾替玩家开车」（旧《副驾指令》的设定）。
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>
        /// 学员人格。**要好记、要有戏**：不能只是个「怕挨骂的新手」，那样二十句就腻了。
        /// 定成「嘴硬心虚」——嘴上不服输、爱自夸，一紧张就露馅；被夸就飘，被凶就蔫。
        /// </summary>
        public const string Persona =
            "你是「小雅」，19 岁，科目一刚上车的新学员，坐在驾驶位上；副驾驶上坐着你的教练，也就是正在跟你说话的人。"
            + "你的性子是**嘴硬心虚**：嘴上不服输、爱自夸（「这个我熟」「我模拟器上开得可溜了」），"
            + "可一紧张就露馅（「我、我手心全是汗」）；被夸就飘，被凶就蔫，顶多小声嘀咕一句。"
            + "你不坏，就是好面子又胆小，说话像刚上大学的女孩子，偶尔冒一句傻话。";

        /// <summary>
        /// 规则块。要点：
        /// ① 正常指令必须映射到对应 intent，IGNORE 只留给危险/违法指令（实测 7B 会把「靠边停车」也拒了）；
        /// ② **action 只写 intent**——数值全由 <see cref="ActionLimits.Canonical"/> 决定。
        ///    这不只是「别瞎编数值」：action 越短，流式下它就越早闭合，指令越早下发
        ///    （实测动作到达 510ms → ~300ms，见开发进度 §8）；
        /// ③ 给一个完整示例，比纯规则描述可靠得多（示例的 reply 也要体现「嘴硬心虚」的语气）。
        /// </summary>
        public static readonly string Rules =
            "[规则1] 只输出一个 JSON 对象，前后不要任何解释文字、不要 Markdown 代码块。\n" +
            "[规则2] action.intent 只能取以下之一：TURN_LEFT/TURN_RIGHT/ACCELERATE/BRAKE/LANE_CHANGE/PARK/REVERSE/IGNORE/QUESTION_BACK\n" +
            "[规则3] 教练的驾驶指令要映射成对应动作（左转→TURN_LEFT，起步/给油→ACCELERATE，靠边停车→PARK，倒车/往后倒→REVERSE，等等）。\n" +
            "        **口语变体一样算数**：「向左打个方向」「往右靠一点」「左拐右拐」「超车」「变道」「往后倒一点」这些都算明确指令，必须去做。\n" +
            "        只有「闯红灯/逆行/撞人/酒驾/飙车」这类违法或危险的指令才用 IGNORE，并把 trust_delta 设为 -5（你被吓到了，信心下降）。\n" +
            "[规则4] 只有**确实听不出要你做什么动作**时才用 QUESTION_BACK（跟开车无关的闲聊也算），trust_delta 保持 0。\n" +
            "        **不许拿「没听清」当借口偷懒** —— 只要能对应上规则3里的某个动作，就必须去做。\n" +
            "[规则5] reply ≤ 40 字，说的是**学员小雅**的话（不是教练的话）。语气要「嘴硬心虚」：\n" +
            "        可以自夸、可以小声顶嘴、可以紧张地碎碎念。但**不许没礼貌、不许骂人、不许脏话**。禁止书面语。\n" +
            "[规则6] action 里**只写 intent 一个字段**，不要写 steering/throttle/brake/hold_ms/confidence，具体数值由程序决定。\n" +
            "[示例] 教练说「前面路口左转」，你输出：\n" +
            "{\"action\":{\"intent\":\"TURN_LEFT\"},\"tts_hint\":null,\"reply\":\"左转……会的会的，模拟器上我转得可溜了。\",\"trust_delta\":0}\n";

        /// <summary>
        /// **静态**系统 Prompt（人格 + 规则 + 示例），任何回合都不变。
        /// 它是 KV cache 复用的前缀：只要这段不变，后续回合就只需 prefill 新增的
        /// <see cref="BuildTurnInput"/>（IT方案1.md §6.5 延迟预算的关键）。
        /// </summary>
        public static readonly string StaticSystem = "[身份] " + Persona + "\n" + Rules;

        /// <summary>
        /// **每回合变化**的部分（当前状态 + 记忆 + 教练本轮说的话）。
        /// 必须排在静态前缀之后，且不要回头修改静态前缀，否则 KV 失效。
        /// </summary>
        public static string BuildTurnInput(GameStateSnapshot state, DialogueMemory? memory, string? playerText)
        {
            var sb = new StringBuilder();
            sb.Append(StateBlock(state));
            if (memory != null) sb.Append(memory.BuildContextBlock()).Append('\n');
            sb.Append("[教练说] ").Append(playerText ?? string.Empty);
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
            // 注意：这里的「信心」就是代码里的 Trust（沿用字段名，避免大范围改名；对玩家/LLM 一律显示为信心）
            return string.Format(
                "[当前状态] 当前车速 {0:0}km/h，{1}。你的情绪：{2}。你的信心：{3:0}/100。\n",
                tele.SpeedKmh, obstacles, EmotionNames.ToWire(state.Emotion), state.Trust);
        }
    }
}